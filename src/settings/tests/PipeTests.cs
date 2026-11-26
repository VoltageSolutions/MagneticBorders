using System.IO.Pipes;
using System.Text;
using MagneticBorders.Settings.Core;
using Xunit;

namespace MagneticBorders.Settings.Tests;

public class PipeResponseParseTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLine_IsEmptyResponse(string line) =>
        Assert.Equal("empty response", PipeResponse.Parse(line).Error);

    [Fact]
    public void NonObject_IsRejected()
    {
        Assert.Equal("response is not an object", PipeResponse.Parse("null").Error);
        Assert.Equal("response is not an object", PipeResponse.Parse("[1,2]").Error);
    }

    [Fact]
    public void OkFalseWithoutError_UsesUnknownError() =>
        Assert.Equal("unknown error", PipeResponse.Parse("{\"ok\":false}").Error);

    [Fact]
    public void MissingOk_IsTreatedAsFailure() =>
        Assert.False(PipeResponse.Parse("{\"engine\":\"cpp\"}").Ok);

    [Fact]
    public void OkWithoutEngine_HasNoStatus()
    {
        var r = PipeResponse.Parse("{\"ok\":true}");
        Assert.True(r.Ok);
        Assert.Null(r.Error);
        Assert.Null(r.Status);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"ok\":true,\"engine\":\"cpp\",\"pid\":\"abc\"}")]
    [InlineData("{\"ok\":\"maybe\"}")]
    public void WrongTypes_AreMalformed(string line)
    {
        var r = PipeResponse.Parse(line);
        Assert.False(r.Ok);
        Assert.Equal("malformed response", r.Error);
    }

    [Fact]
    public void MissingStatusFields_UseDefaults()
    {
        var s = PipeResponse.Parse("{\"ok\":true,\"engine\":\"dotnet\"}").Status!;
        Assert.Equal("dotnet", s.Engine);
        Assert.Equal("", s.Version);
        Assert.Equal(0, s.Pid);
        Assert.True(s.Enabled);
        Assert.True(s.GlueEnabled);
        Assert.Equal(0, s.TolerancePx);
        Assert.Equal(0, s.Drags);
        Assert.Equal(0, s.GluedDrags);
        Assert.Equal(0, s.UptimeSeconds);
    }

    [Fact]
    public void FullStatus_ParsesGlueObject()
    {
        var s = PipeResponse.Parse("{\"ok\":true,\"engine\":\"rust\",\"version\":\"9\",\"pid\":7,\"enabled\":false," +
            "\"glue\":{\"enabled\":false,\"tolerance_px\":21},\"drags\":5,\"glued_drags\":4,\"uptime_s\":99}").Status!;
        Assert.False(s.Enabled);
        Assert.False(s.GlueEnabled);
        Assert.Equal(21, s.TolerancePx);
        Assert.Equal(5, s.Drags);
        Assert.Equal("9", s.Version);
    }

    [Fact]
    public void Failure_BuildsFailedResponse()
    {
        var r = PipeResponse.Failure("boom");
        Assert.False(r.Ok);
        Assert.Equal("boom", r.Error);
        Assert.Null(r.Status);
    }
}


public class PipeEngineClientTests
{
    private static string UniquePipe() => "MagneticBordersTest-" + Guid.NewGuid().ToString("N");

    private static NamedPipeServerStream NewServer(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    /// <summary>Accepts one client, reads its request line, optionally answers with <paramref name="reply"/>, and returns the request.</summary>
    private static Task<string> ServeOnceAsync(NamedPipeServerStream server, string? reply, int holdMs = 0) =>
        Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync() ?? "";
            if (reply is not null)
            {
                await server.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"));
                await server.FlushAsync();
            }
            if (holdMs > 0) await Task.Delay(holdMs);
            return line;
        });

    [Fact]
    public void DefaultPipeName_IsTheSharedContract() =>
        Assert.Equal("MagneticBorders", PipeEngineClient.PipeName);

    [Fact]
    public async Task SendAsync_WritesCommandLine_AndParsesReply()
    {
        var name = UniquePipe();
        using var server = NewServer(name);
        var served = ServeOnceAsync(server, "{\"ok\":true,\"engine\":\"cpp\",\"version\":\"1\",\"pid\":3,\"uptime_s\":8}");
        var r = await new PipeEngineClient(name, 5000).SendAsync("status");
        Assert.Equal("{\"cmd\":\"status\"}", await served);
        Assert.True(r.Ok);
        Assert.Equal("cpp", r.Status!.Engine);
        Assert.Equal(8, r.Status.UptimeSeconds);
    }

    [Fact]
    public async Task SendAsync_ErrorReply_IsReturnedAsFailure()
    {
        var name = UniquePipe();
        using var server = NewServer(name);
        var served = ServeOnceAsync(server, "{\"ok\":false,\"error\":\"unknown command\"}");
        var r = await new PipeEngineClient(name, 5000).SendAsync("bogus");
        await served;
        Assert.False(r.Ok);
        Assert.Equal("unknown command", r.Error);
    }

    [Fact]
    public async Task SendAsync_ServerClosesWithoutReply_IsEmptyResponse()
    {
        var name = UniquePipe();
        using var server = NewServer(name);
        var served = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            await reader.ReadLineAsync();
            server.Disconnect();
        });
        var r = await new PipeEngineClient(name, 5000).SendAsync("status");
        await served;
        Assert.False(r.Ok);
        Assert.Equal("empty response", r.Error);
    }

    [Fact]
    public async Task SendAsync_NoServer_ReportsEngineNotReachable()
    {
        var r = await new PipeEngineClient(UniquePipe(), 150).SendAsync("status");
        Assert.False(r.Ok);
        Assert.Equal("engine not reachable", r.Error);
    }

    [Fact]
    public async Task SendAsync_SlowServer_TimesOut()
    {
        var name = UniquePipe();
        using var server = NewServer(name);
        var served = ServeOnceAsync(server, null, holdMs: 1500);
        var r = await new PipeEngineClient(name, 300).SendAsync("status");
        Assert.False(r.Ok);
        Assert.Equal("engine not reachable", r.Error);
        await served;
    }

    [Fact]
    public async Task SendAsync_CancelledToken_ReportsEngineNotReachable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var r = await new PipeEngineClient(UniquePipe(), 5000).SendAsync("status", cts.Token);
        Assert.False(r.Ok);
        Assert.Equal("engine not reachable", r.Error);
    }
}
