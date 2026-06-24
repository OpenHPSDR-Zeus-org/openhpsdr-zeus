// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;
using Zeus.Dsp.FreeDv;

namespace Zeus.Dsp.FreeDv.Tests;

public class FreeDvModemTests
{
    // These tests deliberately do NOT require the codec2 native library to be
    // present. When it is absent (CI without the native artifact), the modem
    // must degrade to a safe passthrough rather than throw — that contract is
    // what guards the FreeDV audio path from breaking RX/TX on a missing lib.

    [Fact]
    public void DefaultSubmode_Is700D()
    {
        using var modem = new FreeDvModem();
        Assert.Equal(FreeDvSubmode.Mode700D, modem.Submode);
    }

    [Fact]
    public void Inactive_ProcessRx_IsNoOp()
    {
        using var modem = new FreeDvModem();
        var block = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var expected = (float[])block.Clone();
        modem.ProcessRxInPlace(block); // not active
        Assert.Equal(expected, block);
    }

    [SkippableFact]
    public void WhenNativeMissing_ActiveRx_PassesThrough()
    {
        using var modem = new FreeDvModem();
        Skip.If(modem.NativeAvailable, "codec2 native library present — passthrough path not exercised.");

        modem.Activate();
        Assert.True(modem.Active);

        var block = new float[480];
        for (int i = 0; i < block.Length; i++) block[i] = MathF.Sin(i * 0.1f);
        var expected = (float[])block.Clone();

        modem.ProcessRxInPlace(block);
        modem.ProcessTxInPlace(block);
        // No native modem: both directions leave the buffer untouched.
        Assert.Equal(expected, block);
    }

    [Fact]
    public void SetSubmode_UpdatesSubmode()
    {
        using var modem = new FreeDvModem();
        modem.SetSubmode(FreeDvSubmode.Mode700E);
        Assert.Equal(FreeDvSubmode.Mode700E, modem.Submode);
    }

    [Fact]
    public void Activate_Deactivate_TogglesActive()
    {
        using var modem = new FreeDvModem();
        modem.Activate();
        Assert.True(modem.Active);
        modem.Deactivate();
        Assert.False(modem.Active);
    }

    [Fact]
    public void FlushTx_DoesNotThrow_WhenIdle()
    {
        using var modem = new FreeDvModem();
        modem.FlushTx();
        modem.Activate();
        modem.FlushTx();
    }

    [Fact]
    public void RadeV1_ReportsUnavailable_AndPassesThrough()
    {
        // RADE V1 has no native decoder yet. Selecting it must NOT mis-open a
        // classic codec2 handle (which would emit garbage on a RADE signal) — it
        // runs as a clean passthrough and reports RadeAvailable=false so the UI
        // can gate it. This holds whether or not codec2 native is present.
        using var modem = new FreeDvModem();
        modem.SetSubmode(FreeDvSubmode.RadeV1);
        Assert.Equal(FreeDvSubmode.RadeV1, modem.Submode);
        Assert.False(modem.RadeAvailable);

        modem.Activate();
        Assert.True(modem.Active);
        Assert.False(modem.Synced);

        var block = new float[480];
        for (int i = 0; i < block.Length; i++) block[i] = MathF.Sin(i * 0.1f);
        var expected = (float[])block.Clone();
        modem.ProcessRxInPlace(block);
        Assert.Equal(expected, block); // passthrough — no decode, buffer untouched
    }
}
