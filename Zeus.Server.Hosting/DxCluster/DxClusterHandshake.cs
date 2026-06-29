// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.DxCluster;

/// <summary>
/// Pure login/handshake state machine for a DX cluster. No I/O: it is fed each
/// incoming line and returns the lines that should be sent in response (without
/// trailing CRLF — the caller frames them). It is idempotent: the callsign /
/// password / login commands are each sent at most once, no matter how many
/// times a prompt re-appears.
///
/// Recognised prompts (case-insensitive substring match):
///   • callsign — "login:", "call:", "please enter your call", "enter your call",
///     "your call", "callsign:".
///   • password — "password:", "password>", "enter password".
///
/// Login commands (if any) are sent once, on the first line received after the
/// callsign (and password, when one is configured) have been sent — i.e. once we
/// are past the login exchange and the node is talking to us.
/// </summary>
public sealed class DxClusterHandshake
{
    private static readonly string[] CallPrompts =
    {
        "login:", "callsign:", "call:", "please enter your call",
        "enter your call", "your call",
    };

    private static readonly string[] PasswordPrompts =
    {
        "password:", "password>", "enter password",
    };

    private readonly string _callsign;
    private readonly string? _password;
    private readonly string[] _loginCommands;

    private bool _callsignSent;
    private bool _passwordSent;
    private bool _loginCommandsSent;

    public DxClusterHandshake(string callsign, string? password, IEnumerable<string>? loginCommands)
    {
        _callsign = (callsign ?? "").Trim();
        _password = string.IsNullOrWhiteSpace(password) ? null : password;
        _loginCommands = (loginCommands ?? Array.Empty<string>())
            .Select(c => c?.Trim() ?? "")
            .Where(c => c.Length > 0)
            .ToArray();
    }

    private bool PasswordRequired => _password is not null;

    /// <summary>True once the callsign has been sent (login exchange underway).</summary>
    public bool CallsignSent => _callsignSent;

    /// <summary>
    /// Feed one received line. Returns zero or more lines to send back. The
    /// returned strings carry no line terminator.
    /// </summary>
    public IReadOnlyList<string> OnLine(string? line)
    {
        if (line is null)
            return Array.Empty<string>();

        var lower = line.ToLowerInvariant();

        // 1) Callsign at a login/call prompt — once.
        if (!_callsignSent && _callsign.Length > 0 && ContainsAny(lower, CallPrompts))
        {
            _callsignSent = true;
            return new[] { _callsign };
        }

        // 2) Password at a password prompt — once, only if one is configured.
        if (PasswordRequired && !_passwordSent && ContainsAny(lower, PasswordPrompts))
        {
            _passwordSent = true;
            return new[] { _password! };
        }

        // 3) Post-login commands — once, on the first line after the login
        //    exchange completed. Guarded so they never fire before login.
        if (!_loginCommandsSent
            && _loginCommands.Length > 0
            && _callsignSent
            && (!PasswordRequired || _passwordSent))
        {
            _loginCommandsSent = true;
            return _loginCommands;
        }

        return Array.Empty<string>();
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.Ordinal))
                return true;
        return false;
    }
}
