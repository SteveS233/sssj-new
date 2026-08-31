using SssjServer.Core;
using SssjServer.Game;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 登录链路单测：通过内部处理函数 + FakeSession 断言 pf / serial / 加密时序 / Ntf 字段。
/// 依赖注入到 GameModule 内部方法，故仅需临时数据目录。
/// </summary>
public class LoginFlowTests : IDisposable
{
    private readonly string _dataDir;
    private readonly GameModule _module;
    private readonly FakeSession _session = new() { Id = 42 };

    public LoginFlowTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sssj-game-tests", Guid.NewGuid().ToString("N"));
        var options = new ServerOptions
        {
            DataDirectory = _dataDir,
            Aid = 1,
            Sid = 7,
        };
        _module = new GameModule(options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Verify_RepliesOnPf1102_WithRequestSerial_AndEnablesCryptoOnlyAfter()
    {
        const uint serial = 123;
        var req = new VerifyReq { Platform = "selftest", Account = "acc_alpha", Unityplat = 11 };
        var packet = new Packet { Pf = GamePf.Verify, Serial = serial, Payload = req.ToByteArray() };

        _module.HandleVerify(_session, packet);

        // 1) VerifyRsp 发在 pf=1102，serial = 请求 serial（明文阶段）
        var outbound = _session.Sent.Single(o => o.Pf == GamePf.Verify);
        Assert.Equal(serial, outbound.Serial);
        var rsp = Assert.IsType<VerifyRsp>(outbound.Message);
        Assert.Equal(0u, rsp.Retid);
        Assert.False(rsp.Createrole);
        Assert.True(rsp.ClientKey.Length >= 32, "client_key 必须 ≥ 32 字节");
        Assert.Equal("acc_alpha", rsp.Account);
        Assert.Equal(World.DeriveRoleId("acc_alpha"), rsp.Roleid);
        Assert.Equal(1, rsp.Aid);
        Assert.Equal(7, rsp.Sid);

        // 2) 顺序：先发 VerifyRsp，后 EnableCrypto
        Assert.Equal(2, _session.Events.Count);
        Assert.Equal("Send:1102:123", _session.Events[0]);
        Assert.StartsWith("EnableCrypto:", _session.Events[1]);
        Assert.True(_session.CryptoEnabled);
        Assert.Equal(rsp.ClientKey, _session.ClientKey);
    }

    [Fact]
    public async Task Login_RepliesOnPf1002_AndPushesPlayerNtfOnPf1005()
    {
        const uint serial = 555;
        var login = new LoginReq { Account = "acc_beta", Channel = "selftest" };
        var packet = new Packet { Pf = GamePf.Login, Serial = serial, Payload = login.ToByteArray() };

        await _module.HandleLogin(_session, packet);

        // 1) pf=1002 + 请求 serial：LoginRsp
        var loginRspOut = _session.Sent.Single(o => o.Pf == GamePf.LoginRsp);
        Assert.Equal(serial, loginRspOut.Serial);
        var loginRsp = Assert.IsType<LoginRsp>(loginRspOut.Message);
        Assert.Equal(0u, loginRsp.Retid);
        Assert.True(loginRsp.Optime > 0);
        Assert.False(loginRsp.HasStrret, "成功时留空 strret");

        // 2) pf=1005 (serial=0)：PlayerNtf 推送
        var ntfOut = _session.Sent.Single(o => o.Pf == GamePf.PlayerNtf);
        Assert.Equal(0u, ntfOut.Serial);
        var ntf = Assert.IsType<PlayerNtf>(ntfOut.Message);
        Assert.Equal(World.DeriveRoleId("acc_beta"), ntf.Roleid);
        Assert.Equal("acc_beta", ntf.Name);
        Assert.True(ntf.HasServertime && ntf.HasStarttime && ntf.HasRegisterTime && ntf.HasServerzone);

        // 3) 顺序：PlayerNtf(1005) 必须在 LoginRsp(1002) 之前。
        //    pf=1002 的 Game.Login.PlayerLogineRsp 会调 OnEnterGame()，它读 Game.Player.me，
        //    而 me 是 pf=1005 的 PlayerLogined → Game.Player.Login(ntf) 才建立的。
        //    反序会让客户端在 OnEnterGame 抛 NullReferenceException（真机实测）。
        Assert.True(_session.Events.IndexOf("Push:1005") < _session.Events.IndexOf("Send:1002:555"),
            "PlayerNtf(1005) 必须先于 LoginRsp(1002)");

        // 4) 玩家已登录在线
        Assert.NotNull(_module.World.FindOnlineByAccount("acc_beta"));
    }

    [Fact]
    public void BuildPlayerNtf_RoundTrips_ThroughProtocolLayer()
    {
        var player = new Player("acc_gamma", World.DeriveRoleId("acc_gamma"))
        {
            Name = "伽马号",
            Level = 12,
            Exp = 345,
            Fightpower = 6789,
            RegisterTime = 1000,
            StartTime = 2000,
            ServerZone = 7,
            HeadFrame = 3,
            ChatChannelId = 4,
            ClubId = 99,
        };
        player.AddMoney(1, 150);
        player.AddMoney(2, 88);
        player.Girls.Add(new Girl { Girlid = 101, Level = 5, Exp = 10, Modelid = 7, Moodvalue = 90, Vigor = 20, Flag = 1 });

        var ntf = _module.BuildPlayerNtf(player);
        var bytes = ntf.ToByteArray();
        var parsed = ProtoMessage.Parse<PlayerNtf>(bytes);

        Assert.Equal(player.Roleid, parsed.Roleid);
        Assert.Equal(player.Name, parsed.Name);
        Assert.Equal(player.Level, parsed.Level);
        Assert.Equal(player.Exp, parsed.Exp);
        Assert.Equal(player.Fightpower, parsed.Fightpower);
        Assert.Equal(player.RegisterTime, parsed.RegisterTime);
        Assert.Equal(player.StartTime, parsed.Starttime);
        Assert.Equal((ulong)player.ServerZone, (ulong)parsed.Serverzone);
        Assert.True(parsed.HasServertime);
        Assert.Equal(player.HeadFrame, parsed.Headframe);
        Assert.Equal(player.ChatChannelId, parsed.Chatchannelid);
        Assert.Equal(player.ClubId, parsed.Clubid);

        // money round-trip
        Assert.Equal(2, parsed.Moneyinfo.Count);
        var m1 = parsed.Moneyinfo.Single(m => m.Id == 1);
        Assert.Equal(150, m1.Curnum);
        var m2 = parsed.Moneyinfo.Single(m => m.Id == 2);
        Assert.Equal(88, m2.Curnum);

        // girllist round-trip
        Assert.True(parsed.HasGirllist);
        var g = Assert.Single(parsed.Girllist!.Girls);
        Assert.Equal(101u, g.Girlid);
        Assert.Equal(5u, g.Level);
        Assert.Equal(90u, g.Moodvalue);
    }

    [Fact]
    public void KeepAlive_RepliesOnPf1009_WithGsProtoMessage()
    {
        var packet = new Packet { Pf = GamePf.Alive, Serial = 3, Payload = Array.Empty<byte>() };
        _module.HandleKeepAlive(_session, packet);

        var outbound = _session.Sent.Single(o => o.Pf == GamePf.KeepAlive);
        Assert.Equal(3u, outbound.Serial);
        // 必须用 GsProto.KeepAlive（而非 XgSdk 同名类）
        Assert.IsType<KeepAlive>(outbound.Message);
    }

    [Fact]
    public void TimeSync_RepliesOnPf1106_WithServertimeAndZone()
    {
        var packet = new Packet { Pf = GamePf.Time, Serial = 9, Payload = Array.Empty<byte>() };
        _module.HandleTimeSync(_session, packet);

        var outbound = _session.Sent.Single(o => o.Pf == GamePf.TimeRsp);
        Assert.Equal(9u, outbound.Serial);
        var rsp = Assert.IsType<TimeRsp>(outbound.Message);
        Assert.True(rsp.Servertime > 0);
        Assert.Equal(7, rsp.Serverzone);
    }

    [Fact]
    public void GetHouseInfo_RepliesOnPf1049()
    {
        var req = new GetHouseInfoReq { Roleid = World.DeriveRoleId("acc_alpha") };
        var packet = new Packet { Pf = GamePf.GetHouseInfo, Serial = 1, Payload = req.ToByteArray() };
        _module.HandleGetHouseInfo(_session, packet);

        var outbound = _session.Sent.Single(o => o.Pf == GamePf.GetHouseInfoRsp);
        Assert.Equal(1u, outbound.Serial);
        var rsp = Assert.IsType<GetHouseInfoRsp>(outbound.Message);
        Assert.Equal(req.Roleid, rsp.Roleid);
    }

    [Fact]
    public void HouseRandomList_RepliesOnPf1110_Empty()
    {
        var packet = new Packet { Pf = GamePf.HouseRandomList, Serial = 2, Payload = Array.Empty<byte>() };
        _module.HandleHouseRandomList(_session, packet);

        var outbound = _session.Sent.Single(o => o.Pf == GamePf.HouseRandomListRsp);
        Assert.Equal(2u, outbound.Serial);
        var rsp = Assert.IsType<HouseRandomListRsp>(outbound.Message);
        Assert.Empty(rsp.Rolelist);
    }

    [Fact]
    public async Task C2SCall_AcksOnPf1023_WithEmptyErrMsg()
    {
        var req = new C2SCallReq
        {
            SFunction = "LuaCall",
            SJsonParam = "{\"sCmd\":11,\"tbParam\":{\"page\":1}}",
        };
        var packet = new Packet { Pf = GamePf.C2SCall, Serial = 77, Payload = req.ToByteArray() };
        await _module.HandleC2SCall(_session, packet);

        var outbound = _session.Sent.Single(o => o.Pf == GamePf.C2SCallRsp);
        Assert.Equal(77u, outbound.Serial);
        var rsp = Assert.IsType<C2SCallRsp>(outbound.Message);
        Assert.Equal("", rsp.SErrMsg);
    }
}
