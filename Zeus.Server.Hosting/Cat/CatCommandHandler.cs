// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server.Cat;

/// <summary>
/// Stateless-per-connection Kenwood TS-2000 command dispatcher. Holds the
/// per-session Auto-Information level and routes each parsed command to the
/// verified Zeus seams. Deliberately decoupled from socket I/O (it takes a
/// <c>send</c> callback and a <c>latestRxDbm</c> accessor) so the full Tier-1
/// command surface — including the safety-critical "no auto-key" and per-source
/// MOX-ownership behaviour — is unit-testable without a TCP connection.
///
/// All keying goes through <see cref="TxService.TrySetMox"/> with
/// <see cref="MoxSource.Cat"/>; CAT never arms PureSignal and only keys on an
/// explicit <c>TX;</c>.
/// </summary>
internal sealed class CatCommandHandler
{
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly CatOptions _options;
    private readonly Func<double> _latestRxDbm;
    private readonly Action<string> _send;

    private int _autoInfo;

    public CatCommandHandler(
        RadioService radio,
        TxService tx,
        CatOptions options,
        Func<double> latestRxDbm,
        Action<string> send)
    {
        _radio = radio;
        _tx = tx;
        _options = options;
        _latestRxDbm = latestRxDbm;
        _send = send;
    }

    /// <summary>True once the client issued AI1/AI2 — gates unsolicited pushes.</summary>
    public bool AutoInfoEnabled => Volatile.Read(ref _autoInfo) > 0;

    public void Dispatch(string token)
    {
        string cmd = CatProtocol.CommandId(token);
        string args = CatProtocol.Args(token);
        switch (cmd)
        {
            case "ID": _send("ID019;"); break;                            // TS-2000 id (Hamlib requires first)
            case "PS": _send("PS1;"); break;                              // power on
            case "AI": HandleAi(args); break;
            case "FA": HandleFreq(args, vfoB: false); break;
            case "FB": HandleFreq(args, vfoB: true); break;
            case "MD": HandleMode(args); break;
            case "IF": HandleIf(); break;
            case "TX": _tx.TrySetMox(true, MoxSource.Cat, out _); break;   // explicit key only
            case "RX": _tx.TrySetMox(false, MoxSource.Cat, out _); break;
            case "FR": HandleFrFt(args, "FR"); break;                     // RX VFO / split (report-only Tier-1)
            case "FT": HandleFrFt(args, "FT"); break;
            case "SM": HandleSmeter(); break;
            case "PC": HandlePc(args); break;
            default: _send(CatProtocol.Error); break;                     // "?;" — Kenwood unknown/unsupported
        }
    }

    private void HandleAi(string args)
    {
        if (args.Length == 0)
        {
            _send(CatProtocol.Response("AI", Volatile.Read(ref _autoInfo).ToString()));
            return;
        }
        if (int.TryParse(args.AsSpan(0, 1), out int level))
        {
            Volatile.Write(ref _autoInfo, level);
            // On enabling, optionally seed current state so the client need not
            // poll. RX/status only — never a TX frame, never a key.
            if (level > 0 && _options.SendInitialStateOnConnect)
                HandleIf();
        }
    }

    private void HandleFreq(string args, bool vfoB)
    {
        string cmd = vfoB ? "FB" : "FA";
        if (args.Length == 0)
        {
            var state = _radio.Snapshot();
            // VFO B lives in the Receivers projection at index 1 (the flat
            // VFO-B fields were retired in the A/B wire collapse); fall back to
            // VFO A when there is no second receiver.
            long f = state.VfoHz;
            if (vfoB)
            {
                var rxs = state.Receivers;
                f = rxs is not null && rxs.Count > 1 ? rxs[1].VfoHz : state.VfoHz;
            }
            _send(CatProtocol.Response(cmd, CatProtocol.FormatFreq(f)));
            return;
        }
        if (CatProtocol.TryParseFreq(args, out long hz) && hz > 0)
        {
            // External source — bypass the frozen-NCO recenter heuristic so the
            // hardware tracks the commanded frequency absolutely (issue #461,
            // same as TCI). Kenwood set commands have no reply.
            if (vfoB) _radio.SetVfoB(hz);
            else _radio.SetVfo(hz, fromExternal: true);
        }
    }

    private void HandleMode(string args)
    {
        if (args.Length == 0)
        {
            _send(CatProtocol.Response("MD", CatProtocol.ModeDigit(_radio.Snapshot().Mode)));
            return;
        }
        var mode = CatProtocol.ParseMode(args[..1]);
        if (mode is not null) _radio.SetMode(mode.Value);
    }

    private void HandleIf()
    {
        var state = _radio.Snapshot();
        _send(CatProtocol.Response("IF",
            CatProtocol.BuildIfBody(state.VfoHz, state.Mode, _tx.IsMoxOn, split: false)));
    }

    private void HandleFrFt(string args, string cmd)
    {
        // Zeus has no true split seam yet (RIT/XIT/split are Tier-2). Report RX
        // and TX both on VFO A (no split); accept a set without error so clients
        // that probe split don't choke. WSJT-X "Fake It" needs no split.
        if (args.Length == 0) _send(CatProtocol.Response(cmd, "0"));
    }

    private void HandleSmeter()
    {
        // SM reply: "SM" + P1(meter 0=main) + 4-digit value (0000-0030).
        _send(CatProtocol.Response("SM", "0" + CatProtocol.SMeterField(_latestRxDbm())));
    }

    private void HandlePc(string args)
    {
        if (args.Length == 0)
        {
            int cur = Math.Clamp(_radio.Snapshot().DrivePct, 0, 100);
            _send(CatProtocol.Response("PC", cur.ToString().PadLeft(3, '0')));
            return;
        }
        if (int.TryParse(args, out int set))
        {
            set = Math.Clamp(set, 0, 100);
            if (_options.LimitPowerLevels) set = Math.Min(set, 50);
            _radio.SetDrive(set);
        }
    }
}
