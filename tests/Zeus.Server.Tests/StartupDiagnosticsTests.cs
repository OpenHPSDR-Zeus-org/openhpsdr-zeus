// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using OpenhpsdrZeus;

namespace Zeus.Server.Tests;

public class StartupDiagnosticsTests
{
    [Fact]
    public void ParseWindowsCrashEvents_ExtractsApplicationErrorDetails()
    {
        var xml = """
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
  <System>
    <Provider Name="Application Error" />
    <EventID>1000</EventID>
    <TimeCreated SystemTime="2026-06-26T18:24:14.1234567Z" />
    <EventRecordID>4242</EventRecordID>
  </System>
  <EventData>
    <Data>OpenhpsdrZeus.exe</Data>
    <Data>1.2.3.4</Data>
    <Data>abcdef00</Data>
    <Data>Photino.Native.dll</Data>
    <Data>4.0.16.0</Data>
    <Data>abcdef01</Data>
    <Data>c0000005</Data>
    <Data>0000000000012345</Data>
    <Data>0x4f8</Data>
    <Data>0x1db66</Data>
    <Data>C:\Users\Ham\AppData\Local\Programs\OpenHPSDR-Zeus\OpenhpsdrZeus.exe</Data>
    <Data>C:\Users\Ham\AppData\Local\Programs\OpenHPSDR-Zeus\Photino.Native.dll</Data>
    <Data>{8df12f8f-768a-49e4-a7be-000000000001}</Data>
  </EventData>
</Event>
""";

        var events = StartupDiagnostics.ParseWindowsCrashEvents(xml, "OpenhpsdrZeus.exe");

        var crash = Assert.Single(events);
        Assert.Equal("Application Error", crash.Provider);
        Assert.Equal(1000, crash.EventId);
        Assert.Equal("4242", crash.RecordId);
        Assert.Contains("faulting.app=OpenhpsdrZeus.exe", crash.DetailLines);
        Assert.Contains("faulting.module=Photino.Native.dll", crash.DetailLines);
        Assert.Contains("exception.code=c0000005", crash.DetailLines);
        Assert.Contains("app.path=C:\\Users\\Ham\\AppData\\Local\\Programs\\OpenHPSDR-Zeus\\OpenhpsdrZeus.exe", crash.DetailLines);
    }

    [Fact]
    public void ParseWindowsCrashEvents_ExtractsDotNetRuntimeStack()
    {
        var xml = """
<?xml version="1.0" encoding="utf-16"?>
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
  <System>
    <Provider Name=".NET Runtime" />
    <EventID>1026</EventID>
    <TimeCreated SystemTime="2026-06-26T18:25:00.0000000Z" />
    <EventRecordID>4243</EventRecordID>
  </System>
  <EventData>
    <Data>Application: OpenhpsdrZeus.exe
CoreCLR Version: 10.0.0
Description: The process was terminated due to an unhandled exception.
Exception Info: System.InvalidOperationException: boom
   at OpenhpsdrZeus.Program.RunDesktop(System.String[] args)</Data>
  </EventData>
</Event>
""";

        var events = StartupDiagnostics.ParseWindowsCrashEvents(xml, "OpenhpsdrZeus.exe");

        var crash = Assert.Single(events);
        Assert.Equal(".NET Runtime", crash.Provider);
        Assert.Equal(1026, crash.EventId);
        Assert.Contains(crash.DetailLines, line => line.Contains("System.InvalidOperationException: boom"));
        Assert.Contains(crash.DetailLines, line => line.Contains("OpenhpsdrZeus.Program.RunDesktop"));
    }

    [Fact]
    public void ParseWindowsCrashEvents_IgnoresOtherApplications()
    {
        var xml = """
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
  <System>
    <Provider Name="Application Error" />
    <EventID>1000</EventID>
    <TimeCreated SystemTime="2026-06-26T18:24:14.1234567Z" />
    <EventRecordID>4242</EventRecordID>
  </System>
  <EventData>
    <Data>OtherApp.exe</Data>
    <Data>1.2.3.4</Data>
  </EventData>
</Event>
""";

        var events = StartupDiagnostics.ParseWindowsCrashEvents(xml, "OpenhpsdrZeus.exe");

        Assert.Empty(events);
    }
}
