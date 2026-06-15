// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { useCallback, useEffect, useState, type CSSProperties } from 'react';
import {
  createHardwareDiagnosticsMarker,
  fetchHardwareDiagnostics,
  fetchHardwareKeyingStatus,
  fetchRadioDigInDiagnostics,
  fetchRadioNetworkProfile,
  fetchRadioSupplyAlarms,
  fetchRadios,
  fetchSmartNrCondition,
  fetchTxDiagnostics,
  fetchUserIoActions,
  fetchUserIoLabels,
  resetHardwareDiagnosticsMap,
  type HardwareByteStreamMapDto,
  type HardwareDiagnosticItemDto,
  type HardwareDiagnosticsDto,
  type HardwareFeatureSurfaceDto,
  type HardwareKeyingStatusDto,
  type HardwareMappingMarkerDto,
  type HardwareP1MapDto,
  type HardwarePureSignalDiagnosticsDto,
  type RadioNetworkCountersDto,
  type RadioDigInDiagnosticsDto,
  type RadioNetworkProfileDto,
  type RadioInfoDto,
  type RadioSupplyAlarmsDto,
  type SmartNrConditionDto,
  type RadioSupplyReadingDto,
  type TxDiagnosticsDto,
  type UserIoActionsDto,
  type UserIoLabelsDto,
  type UserIoLineDto,
} from '../api/client';

const INVENTORY_INTERVAL_MS = 10_000;

type Field = {
  label: string;
  value: string | number | null | undefined;
};

function dash(v: string | number | null | undefined): string {
  if (v === null || v === undefined || v === '') return '-';
  return String(v);
}

function boolLabel(v: boolean | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return v ? 'ON' : 'OFF';
}

function hex(v: number | null | undefined, width = 2): string {
  if (v === null || v === undefined) return '-';
  return `0x${Math.max(0, v).toString(16).toUpperCase().padStart(width, '0')}`;
}

function adc(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return `${v} / ${hex(v, 4)}`;
}

function time(v: string | null | undefined): string {
  if (!v) return '-';
  const d = new Date(v);
  return Number.isNaN(d.getTime()) ? v : d.toLocaleTimeString();
}

function age(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  if (v < 1000) return `${Math.max(0, Math.round(v))} ms`;
  return `${Math.round(v / 1000)} s`;
}

function db(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return `${v.toFixed(1)} dB`;
}

function pct(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return `${v.toFixed(1)}%`;
}

function count(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return Number.isFinite(v) ? Math.round(v).toLocaleString() : '-';
}

function volts(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return `${v.toFixed(2)} V`;
}

function hz(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  if (!Number.isFinite(v)) return '-';
  if (v >= 1_000_000) {
    const mhz = v / 1_000_000;
    return `${Number.isInteger(mhz) ? mhz.toFixed(0) : mhz.toFixed(3)} MHz`;
  }
  if (v >= 1000) return `${v / 1000} kHz`;
  return `${v} Hz`;
}

function FieldGrid({ fields }: { fields: Field[] }) {
  return (
    <div
      style={{
        display: 'grid',
        gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
        gap: 8,
      }}
    >
      {fields.map((f) => (
        <div
          key={f.label}
          style={{
            padding: '7px 8px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div
            style={{
              fontSize: 9,
              fontWeight: 700,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
              color: 'var(--fg-3)',
            }}
          >
            {f.label}
          </div>
          <div
            className="mono"
            style={{
              marginTop: 3,
              fontSize: 12,
              color: 'var(--fg-0)',
              overflowWrap: 'anywhere',
            }}
          >
            {dash(f.value)}
          </div>
        </div>
      ))}
    </div>
  );
}

function DiagnosticRecommendation({ text }: { text: string | null | undefined }) {
  if (!text) return null;
  return (
    <div style={{ marginTop: 8, fontSize: 11, lineHeight: 1.35, color: 'var(--fg-2)' }}>
      {text}
    </div>
  );
}

function NetworkCounters({
  label,
  counters,
}: {
  label: string;
  counters: RadioNetworkCountersDto;
}) {
  return (
    <div style={{ display: 'grid', gap: 7 }}>
      <span style={{ fontSize: 11, fontWeight: 800, color: 'var(--fg-0)' }}>{label}</span>
      <FieldGrid
        fields={[
          { label: 'Attached', value: boolLabel(counters.attached) },
          { label: 'Frames', value: counters.totalFrames },
          { label: 'Drops', value: counters.droppedFrames },
          { label: 'Drop Ratio', value: pct(counters.dropRatioPct) },
          { label: 'Hi Priority', value: counters.hiPriorityPackets },
          { label: 'PS Paired', value: counters.psPairedPackets },
        ]}
      />
    </div>
  );
}

function HardwareKeyingDiagnostics({ status }: { status: HardwareKeyingStatusDto | null }) {
  if (!status) return <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>Waiting for keying diagnostics.</div>;
  const ext = status.externalPtt;
  return (
    <div style={{ display: 'grid', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Protocol', value: status.activeProtocol },
          { label: 'P1 Packets', value: status.p1Packets },
          { label: 'P1 Updated', value: time(status.p1LastUpdatedUtc) },
          { label: 'P1 HW PTT', value: boolLabel(status.p1HardwarePtt) },
          { label: 'P1 CW Key', value: boolLabel(status.p1CwKeyDown) },
          { label: 'P2 Packets', value: status.p2Packets },
          { label: 'P2 Updated', value: time(status.p2LastUpdatedUtc) },
          { label: 'P2 PTT', value: boolLabel(status.p2PttIn) },
          { label: 'P2 Dot', value: boolLabel(status.p2DotIn) },
          { label: 'P2 Dash', value: boolLabel(status.p2DashIn) },
          { label: 'P2 Sidetone', value: boolLabel(status.p2SidetoneActive) },
          { label: 'Generated', value: time(status.generatedUtc) },
        ]}
      />
      <FieldGrid
        fields={[
          { label: 'External PTT', value: boolLabel(ext.available) },
          { label: 'PTT Protocol', value: ext.protocol },
          { label: 'Owns MOX', value: boolLabel(ext.ownedMox) },
          { label: 'MOX Owner', value: ext.moxOwner },
          { label: 'Hang', value: `${ext.hangTimeMs} ms` },
          { label: 'MOX', value: boolLabel(ext.moxOn) },
          { label: 'TUN', value: boolLabel(ext.tunOn) },
          { label: 'Two Tone', value: boolLabel(ext.twoToneOn) },
          { label: 'CW Mode', value: boolLabel(ext.cwMode) },
          { label: 'Sidetone', value: boolLabel(ext.sidetoneAvailable) },
        ]}
      />
      <DiagnosticRecommendation text={status.diagnosticRecommendation ?? ext.diagnosticRecommendation} />
    </div>
  );
}

function SupplyReading({
  label,
  reading,
}: {
  label: string;
  reading: RadioSupplyReadingDto;
}) {
  return (
    <div style={{ display: 'grid', gap: 7 }}>
      <span style={{ fontSize: 11, fontWeight: 800, color: 'var(--fg-0)' }}>{label}</span>
      <FieldGrid
        fields={[
          { label: 'Packets', value: reading.packets },
          { label: 'Updated', value: time(reading.lastUpdatedUtc) },
          { label: 'Supply ADC', value: adc(reading.supplyVoltsAdc) },
          { label: 'Supply Volts', value: volts(reading.supplyVolts) },
          { label: 'Raw Scaled', value: volts(reading.rawScaledSupplyVolts) },
          { label: 'Scale Status', value: reading.scaleStatus },
          { label: 'Trusted', value: boolLabel(reading.supplyVoltsTrusted) },
        ]}
      />
    </div>
  );
}

function SupplyAlarmDiagnostics({ alarms }: { alarms: RadioSupplyAlarmsDto | null }) {
  if (!alarms) return <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>Waiting for supply telemetry.</div>;
  return (
    <div style={{ display: 'grid', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Protocol', value: alarms.activeProtocol },
          { label: 'Board', value: alarms.effectiveBoard },
          { label: 'Variant', value: alarms.orionMkIIVariant },
          { label: 'Supported', value: boolLabel(alarms.supportsSupplyTelemetry) },
          { label: 'Scale', value: `${alarms.adcSupplyMv} mV/step` },
          { label: 'Thresholds', value: boolLabel(alarms.activeThresholdsConfigured) },
          { label: 'Alarm', value: boolLabel(alarms.alarmActive) },
          { label: 'Status', value: alarms.alarmStatus },
          { label: 'Generated', value: time(alarms.generatedUtc) },
        ]}
      />
      <SupplyReading label="Protocol 1" reading={alarms.p1} />
      <SupplyReading label="Protocol 2" reading={alarms.p2} />
      <DiagnosticRecommendation text={alarms.diagnosticRecommendation} />
    </div>
  );
}

function NetworkProfileDiagnostics({ profile }: { profile: RadioNetworkProfileDto | null }) {
  if (!profile) return <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>Waiting for network profile.</div>;
  return (
    <div style={{ display: 'grid', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Status', value: profile.connectionStatus },
          { label: 'Endpoint', value: profile.endpoint },
          { label: 'Protocol', value: profile.activeProtocol },
          { label: 'Transport', value: profile.transport },
          { label: 'Sample Rate', value: profile.sampleRateHz ? `${profile.sampleRateHz / 1000} kHz` : null },
          { label: 'Connected', value: profile.connectedBoard },
          { label: 'Effective', value: profile.effectiveBoard },
          { label: 'Variant', value: profile.orionMkIIVariant },
          { label: 'Health', value: profile.healthStatus },
          { label: 'Generated', value: time(profile.generatedUtc) },
        ]}
      />
      <NetworkCounters label="Protocol 1 Counters" counters={profile.p1} />
      <NetworkCounters label="Protocol 2 Counters" counters={profile.p2} />
      <DiagnosticRecommendation text={profile.diagnosticRecommendation} />
    </div>
  );
}

function ReceiverTopologyDiagnostics({ diag }: { diag: HardwareDiagnosticsDto | null }) {
  if (!diag) return <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>Waiting for topology diagnostics.</div>;
  const caps = diag.capabilities;
  const p2 = diag.p2;
  const dualAdc = caps.rxAdcCount >= 2;
  const saturnClass = diag.effectiveBoard === 'OrionMkII' && dualAdc && caps.mkiiBpf;
  const g2Class = saturnClass && caps.maxRxSampleRateHz >= 1_536_000;
  const zeusRxSurface = dualAdc ? 'RX1/RX2 exposed' : 'RX1 exposed';
  const manualRxCapacity = g2Class
    ? '10 independent DDC receivers'
    : saturnClass
      ? 'Saturn-class dual DDC topology'
      : 'not applicable';
  const recommendation = g2Class
    ? 'G2 topology is recognized from the manual-backed capability map: dual phase-synchronous ADCs, independent MKII/preselector paths, RX2 stepped attenuation, and the 1.536 MHz DDC ceiling. Zeus exposes RX1/RX2 plus live P2 ADC max/overload telemetry; 10-DDC assignment and ADC2 ground-on-TX remain read-only gaps until protocol mapping is verified.'
    : dualAdc
      ? 'This board exposes a dual-ADC topology. Zeus can monitor both ADC max/overload paths, but any board-specific ADC2 transmit routing should stay read-only until verified for this variant.'
      : 'This board is single-ADC; G2 ADC2 and RX2 routing controls do not apply.';

  return (
    <div style={{ display: 'grid', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Board', value: diag.effectiveBoard },
          { label: '0x0A Variant', value: diag.orionMkIIVariant },
          { label: 'RX ADCs', value: caps.rxAdcCount },
          { label: 'Dual Phase Sync', value: boolLabel(dualAdc) },
          { label: 'Independent Filter Banks', value: boolLabel(dualAdc && caps.mkiiBpf) },
          { label: 'MKII Preselector', value: boolLabel(caps.mkiiBpf) },
          { label: 'RX2 Stepped ATT', value: boolLabel(caps.hasSteppedAttenuationRx2) },
          { label: 'Max DDC BW', value: hz(caps.maxRxSampleRateHz) },
          { label: 'Manual RX Capacity', value: manualRxCapacity },
          { label: 'Zeus RX Surface', value: zeusRxSurface },
          { label: 'ADC2 Ground on TX', value: g2Class ? 'not exposed' : 'not applicable' },
          { label: '6m LNA / Filters', value: caps.mkiiBpf ? 'manual-backed, no separate runtime telemetry' : 'not applicable' },
        ]}
      />
      <FieldGrid
        fields={[
          { label: 'P2 Packets', value: p2.packets },
          { label: 'P2 Updated', value: time(p2.lastUpdatedUtc) },
          { label: 'ADC Overload', value: hex(p2.adcOverloadBits, 2) },
          { label: 'ADC0 Max', value: adc(p2.adc0MaxMagnitude) },
          { label: 'ADC1 Max', value: adc(p2.adc1MaxMagnitude) },
          { label: 'ADC0 Over Max', value: adc(p2.adc0MaxMagnitudeAtOverload) },
          { label: 'ADC1 Over Max', value: adc(p2.adc1MaxMagnitudeAtOverload) },
        ]}
      />
      <DiagnosticRecommendation text={recommendation} />
    </div>
  );
}

function TxEgressDiagnostics({ diag }: { diag: TxDiagnosticsDto | null }) {
  if (!diag) return <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>Waiting for TX diagnostics.</div>;
  const p2 = diag.protocol2;
  const egress = diag.egress;
  const audioPath = diag.audioPath;
  return (
    <div style={{ display: 'grid', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Health', value: egress.healthStatus },
          { label: 'RF Evidence', value: egress.rfEvidenceStatus },
          { label: 'Transport', value: egress.activeTransport },
          { label: 'P2 Attached', value: boolLabel(egress.p2Attached) },
          { label: 'P2 Live', value: boolLabel(egress.p2Live) },
          { label: 'Host TX', value: boolLabel(egress.hostTxActive) },
          { label: 'MOX', value: boolLabel(egress.hostMoxOn) },
          { label: 'TUN', value: boolLabel(egress.hostTunOn) },
          { label: 'Two-Tone', value: boolLabel(egress.hostTwoToneOn) },
          { label: 'Hardware PTT', value: boolLabel(egress.hardwarePtt) },
          { label: 'RF Detected', value: boolLabel(egress.rfDetected) },
          { label: 'Forward Power', value: egress.forwardWatts === null ? null : `${egress.forwardWatts.toFixed(2)} W` },
          { label: 'P2 Activity Age', value: age(egress.p2LastActivityAgeMs) },
          { label: 'P1 Drop Ratio', value: pct(egress.p1RingDropRatioPct) },
          { label: 'Health Updated', value: time(egress.generatedUtc) },
          { label: 'Endpoint Updated', value: time(diag.generatedUtc) },
        ]}
      />
      <FieldGrid
        fields={[
          { label: 'Audio Path', value: audioPath.status },
          { label: 'Path Host TX', value: boolLabel(audioPath.hostTxActive) },
          { label: 'P2 DUC Live', value: boolLabel(audioPath.p2DucLive) },
          { label: 'P2 Waiting', value: boolLabel(audioPath.p2WaitingForTx) },
          { label: 'P2 IQ In', value: count(audioPath.p2InputComplexSamples) },
          { label: 'P2 Sent', value: count(audioPath.p2PacketsSent) },
          { label: 'P2 Queue', value: count(audioPath.p2QueuedPackets) },
          { label: 'P2 Path Age', value: age(audioPath.p2LastActivityAgeMs) },
          { label: 'Path Mic Samples', value: count(audioPath.totalMicSamples) },
          { label: 'Path TX Blocks', value: count(audioPath.totalTxBlocks) },
          { label: 'Path Mic Drops', value: count(audioPath.droppedFrames) },
          { label: 'P1 Fill', value: pct(audioPath.ringFillPct) },
          { label: 'P1 Path Drops', value: count(audioPath.ringDropped) },
          { label: 'P1 Path Drop Ratio', value: pct(audioPath.ringDropRatioPct) },
          { label: 'P1 Path Mag', value: audioPath.ringRecentMag.toFixed(3) },
        ]}
      />
      <FieldGrid
        fields={[
          { label: 'IQ Source', value: diag.iqSourceType?.split('.').at(-1) ?? diag.iqSourceType },
          { label: 'Source Is Ring', value: boolLabel(diag.iqSourceIsRing) },
          { label: 'Mic Samples', value: count(diag.ingest.totalMicSamples) },
          { label: 'TX Blocks', value: count(diag.ingest.totalTxBlocks) },
          { label: 'Mic Drops', value: count(diag.ingest.droppedFrames) },
          { label: 'P1 Written', value: count(diag.ring.totalWritten) },
          { label: 'P1 Read', value: count(diag.ring.totalRead) },
          { label: 'P1 Count', value: count(diag.ring.count) },
          { label: 'P1 Dropped', value: count(diag.ring.dropped) },
          { label: 'P1 Capacity', value: count(diag.ring.capacity) },
          { label: 'P1 Recent Mag', value: diag.ring.recentMag.toFixed(3) },
        ]}
      />
      <FieldGrid
        fields={[
          { label: 'TXA Stage', value: diag.stage.status },
          { label: 'Stage Source', value: diag.stage.source },
          { label: 'Stage Host TX', value: boolLabel(diag.stage.hostTxActive) },
          { label: 'Mic Pk', value: db(diag.stage.micPkDbfs) },
          { label: 'Mic Av', value: db(diag.stage.micAvDbfs) },
          { label: 'EQ Pk', value: db(diag.stage.eqPkDbfs) },
          { label: 'EQ Av', value: db(diag.stage.eqAvDbfs) },
          { label: 'Leveler Pk', value: db(diag.stage.lvlrPkDbfs) },
          { label: 'Leveler Av', value: db(diag.stage.lvlrAvDbfs) },
          { label: 'Leveler GR', value: db(diag.stage.lvlrGrDb) },
          { label: 'CFC Pk', value: db(diag.stage.cfcPkDbfs) },
          { label: 'CFC Av', value: db(diag.stage.cfcAvDbfs) },
          { label: 'CFC GR', value: db(diag.stage.cfcGrDb) },
          { label: 'Comp Pk', value: db(diag.stage.compPkDbfs) },
          { label: 'Comp Av', value: db(diag.stage.compAvDbfs) },
          { label: 'ALC Pk', value: db(diag.stage.alcPkDbfs) },
          { label: 'ALC Av', value: db(diag.stage.alcAvDbfs) },
          { label: 'ALC GR', value: db(diag.stage.alcGrDb) },
          { label: 'Out Pk', value: db(diag.stage.outPkDbfs) },
          { label: 'Out Av', value: db(diag.stage.outAvDbfs) },
        ]}
      />
      <FieldGrid
        fields={[
          { label: 'P2 Attached', value: boolLabel(Boolean(p2)) },
          { label: 'Sender Running', value: boolLabel(p2?.senderRunning) },
          { label: 'IQ Samples In', value: count(p2?.inputComplexSamples) },
          { label: 'Packets Queued', value: count(p2?.packetsQueued) },
          { label: 'Packets Sent', value: count(p2?.packetsSent) },
          { label: 'Queue Depth', value: count(p2?.queuedPackets) },
          { label: 'Write Failures', value: count(p2?.queueWriteFailures) },
          { label: 'Send Failures', value: count(p2?.sendFailures) },
          { label: 'Reset Drains', value: count(p2?.resetDrainedPackets) },
          { label: 'Scratch Samples', value: count(p2?.scratchComplexSamples) },
          { label: 'Next Sequence', value: count(p2?.nextSequence) },
          { label: 'Last Packets/s', value: count(p2?.lastPacketsPerSecond) },
          { label: 'FIFO Model', value: count(p2?.lastFifoModelSamples) },
          { label: 'Rate Updated', value: time(p2?.lastRateTimestampUtc) },
        ]}
      />
      <FieldGrid
        fields={[
          { label: 'Plugin Master Bypass', value: boolLabel(diag.txPlugins?.masterBypassed) },
          { label: 'Remote TX Bypass', value: boolLabel(diag.txPlugins?.bypassedForRemoteTx) },
          { label: 'VST Active', value: boolLabel(diag.vstEngine?.active) },
          { label: 'VST Degraded', value: count(diag.vstEngine?.degradedBlocks) },
        ]}
      />
      <DiagnosticRecommendation text={audioPath.diagnosticRecommendation} />
      <DiagnosticRecommendation text={diag.stage.diagnosticRecommendation} />
      <DiagnosticRecommendation text={egress.diagnosticRecommendation} />
    </div>
  );
}

function PureSignalFeedbackDiagnostics({ diag }: { diag: HardwarePureSignalDiagnosticsDto | null | undefined }) {
  if (!diag) return <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>Waiting for PureSignal diagnostics.</div>;
  const mode = diag.single ? 'Single' : diag.auto ? 'Auto' : 'Manual';
  return (
    <div style={{ display: 'grid', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Armed', value: boolLabel(diag.enabled) },
          { label: 'Source', value: diag.feedbackSource },
          { label: 'External Path', value: boolLabel(diag.externalFeedbackPathSupported) },
          { label: 'RF Bypass Required', value: boolLabel(diag.rfBypassRequired) },
          { label: 'RF Bypass Selected', value: boolLabel(diag.rfBypassSelected) },
          { label: 'Health', value: diag.healthStatus },
          { label: 'Correcting', value: boolLabel(diag.correcting) },
          { label: 'Cal State', value: diag.calState },
          { label: 'Stalled', value: boolLabel(diag.calibrationStalled) },
          { label: 'Monitor PA Output', value: boolLabel(diag.monitorEnabled) },
        ]}
      />
      <FieldGrid
        fields={[
          { label: 'Feedback Raw', value: diag.feedbackLevelRaw.toFixed(1) },
          { label: 'Feedback %', value: pct(diag.feedbackLevelPct) },
          { label: 'Target Raw', value: diag.feedbackTargetRaw.toFixed(1) },
          {
            label: 'Usable Window',
            value: `${diag.feedbackUsableMinRaw}-${diag.feedbackUsableMaxRaw}`,
          },
          {
            label: 'Centered Window',
            value: `${diag.feedbackCenteredMinRaw}-${diag.feedbackCenteredMaxRaw}`,
          },
          { label: 'TX Feedback Attn', value: db(diag.txFeedbackAttenuationDb) },
          { label: 'Attn Floor', value: db(diag.txFeedbackAttenuationDbMin) },
          { label: 'HW Peak', value: diag.hwPeak.toFixed(4) },
          { label: 'HW Peak Default', value: diag.hwPeakDefault.toFixed(4) },
          { label: 'Auto Attn', value: boolLabel(diag.autoAttenuate) },
          { label: 'Cal Mode', value: mode },
        ]}
      />
      <DiagnosticRecommendation text={diag.diagnosticRecommendation} />
      <DiagnosticRecommendation text={diag.manualReference} />
    </div>
  );
}

function UserIoLineRows({ lines }: { lines: UserIoLineDto[] }) {
  if (lines.length === 0) {
    return <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>No user I/O lines decoded yet.</div>;
  }
  return (
    <div style={{ display: 'grid', gap: 8 }}>
      {lines.map((line) => (
        <div
          key={`${line.id}:${line.kind}`}
          style={{
            padding: '8px 10px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
            <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>{line.label}</span>
            <span className="mono" style={{ fontSize: 10, color: 'var(--fg-3)' }}>
              {line.id} / {line.kind}
            </span>
          </div>
          <FieldGrid
            fields={[
              { label: 'Raw ADC', value: adc(line.rawAdc) },
              { label: 'Normalized', value: pct(line.normalizedPct) },
              { label: 'Digital', value: boolLabel(line.digitalState) },
            ]}
          />
        </div>
      ))}
    </div>
  );
}

function UserIoDiagnostics({
  labels,
  actions,
}: {
  labels: UserIoLabelsDto | null;
  actions: UserIoActionsDto | null;
}) {
  if (!labels && !actions) return <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>Waiting for user I/O telemetry.</div>;
  const lines = labels?.lines.length ? labels.lines : actions?.lines ?? [];
  return (
    <div style={{ display: 'grid', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Protocol', value: labels?.activeProtocol ?? actions?.activeProtocol },
          { label: 'P2 Attached', value: boolLabel(labels?.p2Attached ?? actions?.p2Attached) },
          { label: 'P2 Packets', value: labels?.p2Packets ?? actions?.p2Packets },
          { label: 'P2 Updated', value: time(labels?.p2LastUpdatedUtc ?? actions?.p2LastUpdatedUtc) },
          { label: 'Labels', value: labels?.lines.length ?? 0 },
          { label: 'Actions Armed', value: boolLabel(actions?.actionBindingsConfigured) },
          { label: 'Generated', value: time(labels?.generatedUtc ?? actions?.generatedUtc) },
        ]}
      />
      <UserIoLineRows lines={lines} />
      <DiagnosticRecommendation text={actions?.diagnosticRecommendation ?? labels?.diagnosticRecommendation} />
    </div>
  );
}

function DigInDiagnostics({ diag }: { diag: RadioDigInDiagnosticsDto | null }) {
  if (!diag) return <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>Waiting for Dig In telemetry.</div>;
  const inhibitLabel =
    diag.txDisableActive === null
      ? 'UNKNOWN'
      : diag.txDisableActive
        ? 'ACTIVE'
        : 'INACTIVE';
  return (
    <div style={{ display: 'grid', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Protocol', value: diag.activeProtocol },
          { label: 'Board', value: diag.effectiveBoard },
          { label: '0x0A Variant', value: diag.orionMkIIVariant },
          { label: 'P2 Attached', value: boolLabel(diag.p2Attached) },
          { label: 'P2 Packets', value: diag.p2Packets },
          { label: 'P2 Updated', value: time(diag.p2LastUpdatedUtc) },
          { label: 'User DIN', value: hex(diag.userDigitalIn, 2) },
          { label: 'Mapping', value: diag.txDisableMappingStatus },
        ]}
      />
      <FieldGrid
        fields={[
          { label: 'TX Disable', value: inhibitLabel },
          { label: 'Line', value: `${diag.txDisableLineName} / ${diag.txDisableLineId}` },
          { label: 'Bit', value: diag.txDisableBit },
          { label: 'Raw High', value: boolLabel(diag.txDisableRawHigh) },
          { label: 'Polarity', value: diag.txDisablePolarity },
          { label: 'TX Blocking Armed', value: boolLabel(diag.txInhibitBehaviorArmed) },
          { label: 'CW Tip Source', value: diag.cwKeyTipSource },
          { label: 'CW Tip Down', value: boolLabel(diag.cwKeyTipDown) },
          { label: 'Dash Input', value: boolLabel(diag.cwDashInputDown) },
          { label: 'Generated', value: time(diag.generatedUtc) },
        ]}
      />
      <DiagnosticRecommendation text={diag.diagnosticRecommendation} />
      <DiagnosticRecommendation text={diag.manualReference} />
    </div>
  );
}

function detail(radio: RadioInfoDto, key: string): string | null {
  return radio.details?.[key] ?? null;
}

function RadioInventory({ radios }: { radios: RadioInfoDto[] | null }) {
  if (!radios) {
    return (
      <div style={{ fontSize: 12, lineHeight: 1.4, color: 'var(--fg-2)' }}>
        Waiting for discovery.
      </div>
    );
  }
  if (radios.length === 0) {
    return (
      <div style={{ fontSize: 12, lineHeight: 1.4, color: 'var(--fg-2)' }}>
        No OpenHPSDR radios answered the discovery sweep.
      </div>
    );
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      {radios.map((radio) => (
        <div
          key={`${radio.macAddress}:${radio.ipAddress}:${detail(radio, 'protocol') ?? ''}`}
          style={{
            padding: '8px 10px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div
            style={{
              display: 'flex',
              gap: 8,
              alignItems: 'baseline',
              justifyContent: 'space-between',
              flexWrap: 'wrap',
            }}
          >
            <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>
              {radio.boardId}
            </span>
            <span className="mono" style={{ fontSize: 10, color: 'var(--fg-3)' }}>
              {detail(radio, 'protocol') ?? '-'} {radio.busy ? 'BUSY' : 'IDLE'}
            </span>
          </div>
          <FieldGrid
            fields={[
              { label: 'IP', value: radio.ipAddress },
              { label: 'MAC', value: radio.macAddress },
              { label: 'Firmware', value: radio.firmwareVersion },
              { label: 'Raw Board', value: detail(radio, 'rawBoardId') },
              { label: 'Receivers', value: detail(radio, 'numReceivers') },
              { label: 'Protocol Ver', value: detail(radio, 'protocolSupported') },
              { label: 'Gateware', value: detail(radio, 'gatewareBuild') },
              { label: 'HL2 Minor', value: detail(radio, 'hl2MinorVersion') },
              { label: 'Beta', value: detail(radio, 'betaVersion') },
              { label: 'Mercury 0', value: detail(radio, 'mercuryVersion0') },
              { label: 'Mercury 1', value: detail(radio, 'mercuryVersion1') },
              { label: 'Mercury 2', value: detail(radio, 'mercuryVersion2') },
              { label: 'Mercury 3', value: detail(radio, 'mercuryVersion3') },
              { label: 'Penny', value: detail(radio, 'pennyVersion') },
              { label: 'Metis', value: detail(radio, 'metisVersion') },
              { label: 'Raw Reply', value: detail(radio, 'rawReplyHex') },
            ]}
          />
        </div>
      ))}
    </div>
  );
}

function MappingRows({ items }: { items: HardwareDiagnosticItemDto[] }) {
  if (items.length === 0) return null;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      {items.map((item) => (
        <div
          key={`${item.field}:${item.status}`}
          style={{
            padding: '8px 10px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'baseline',
              gap: 8,
              flexWrap: 'wrap',
            }}
          >
            <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>
              {item.field}
            </span>
            <span
              className="mono"
              style={{
                fontSize: 10,
                color: item.status === 'decoded' || item.status === 'mapped'
                  ? 'var(--accent)'
                  : 'var(--power)',
              }}
            >
              {item.status}
            </span>
          </div>
          {item.source && (
            <div className="mono" style={{ marginTop: 4, fontSize: 10, color: 'var(--fg-3)' }}>
              {item.source}
            </div>
          )}
          <div style={{ marginTop: 5, fontSize: 11, lineHeight: 1.35, color: 'var(--fg-2)' }}>
            {item.notes}
          </div>
        </div>
      ))}
    </div>
  );
}

type HardwareOpportunity = {
  item: HardwareFeatureSurfaceDto;
  plannedControls: string[];
  liveControls: string[];
  readiness: string;
  priority: number;
  nextStep: string;
};

function plannedControls(item: HardwareFeatureSurfaceDto): string[] {
  return item.candidateControls
    .filter((control) => control.startsWith('planned:'))
    .map((control) => control.slice('planned:'.length));
}

function liveControls(item: HardwareFeatureSurfaceDto): string[] {
  return item.candidateControls.filter((control) => !control.startsWith('planned:'));
}

function featureNextStep(id: string): string {
  switch (id) {
    case 'hardware.inventory.discovery':
      return 'Add a per-radio Hardware/Network profile for NIC selection, fixed IP, discovery speed, subnet behavior, protocol match, and P2 port notification.';
    case 'rx.auto-attenuation.adc-overload':
      return 'Use the live ADC protection endpoint for per-radio presets, overload history, and profile suggestions based on band noise and ADC headroom.';
    case 'pa.telemetry.power-supply':
      return 'Add calibrated PA/supply meters, per-radio conversion constants, warning thresholds, and supply sag alarms.';
    case 'hardware.user-io':
      return 'Expose user ADC/DIN labels, thresholds, debounce, and safe action bindings after each physical line is correlated.';
    case 'hardware.front-panel.leds':
      return 'Mirror the front-panel LED word as a read-only remote diagnostics strip and use it for UI state reconciliation.';
    case 'cw.hardware-keying':
      return 'Add guarded hardware key/PTT configuration with explicit arming, sidetone policy, and external PTT ownership display.';
    case 'hardware.mapping.correlation':
      return 'Turn the marker API into guided capture recipes so a single radio action can be mapped and promoted to a typed setting.';
    case 'rx.signal-intelligence.weak-signal':
      return 'Use the display-intelligence API and mirrored coherent scene metrics to annotate recordings, compare clients, and gate future server-side weak-signal policy safely.';
    case 'rx.smart-nr.adaptive':
      return 'Correlate Smart NR recommendations with RX-chain health over time so automation can distinguish noise-floor cleanup from ADC/headroom protection.';
    case 'tx.fidelity.spectral-density':
      return 'Use the TX fidelity policy and station profiles to keep target headroom, occupied bandwidth, PureSignal feedback health, and spectral-density warnings tied to the active station profile.';
    default:
      return 'Promote the advertised telemetry into a typed setting only after the hardware path is correlated and gated by board capability.';
  }
}

function rankFeature(item: HardwareFeatureSurfaceDto, plannedCount: number): number {
  const status = item.implementationStatus.toLowerCase();
  let score = 0;
  if (status.includes('telemetry-ready')) score += 4;
  if (status.includes('api-ready')) score += 3;
  if (item.userConfigurable) score += 2;
  if (plannedCount > 0) score += 2;
  if (item.safetyClass === 'rx-safe' || item.safetyClass === 'tx-monitoring-only') score += 1;
  if (item.safetyClass.includes('tx-capable')) score -= 1;
  if (item.id === 'rx.auto-attenuation.adc-overload') score += 2;
  if (item.id === 'rx.signal-intelligence.weak-signal') score += 3;
  if (item.id === 'rx.smart-nr.adaptive') score += 3;
  if (item.id === 'tx.fidelity.spectral-density') score += 3;
  if (item.id === 'pa.telemetry.power-supply') score += 1;
  return score;
}

function readinessLabel(item: HardwareFeatureSurfaceDto, plannedCount: number): string {
  const status = item.implementationStatus.toLowerCase();
  if (plannedCount > 0 && (status.includes('telemetry-ready') || status.includes('api-ready'))) {
    return 'ready to wire';
  }
  if (status.includes('telemetry-ready')) return 'telemetry-ready';
  if (status.includes('api-ready')) return 'api-ready';
  return item.implementationStatus || 'candidate';
}

function buildHardwareOpportunities(items: HardwareFeatureSurfaceDto[]): HardwareOpportunity[] {
  return items
    .map((item) => {
      const planned = plannedControls(item);
      return {
        item,
        plannedControls: planned,
        liveControls: liveControls(item),
        readiness: readinessLabel(item, planned.length),
        priority: rankFeature(item, planned.length),
        nextStep: featureNextStep(item.id),
      };
    })
    .filter((row) => row.item.userConfigurable || row.plannedControls.length > 0 || row.item.id === 'hardware.mapping.correlation')
    .sort((a, b) => b.priority - a.priority || a.item.id.localeCompare(b.item.id));
}

function HardwareOpportunityMatrix({ items }: { items: HardwareFeatureSurfaceDto[] }) {
  if (items.length === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
        No hardware feature surfaces have been advertised yet.
      </div>
    );
  }

  const rows = buildHardwareOpportunities(items);
  const plannedApiCount = new Set(rows.flatMap((row) => row.plannedControls)).size;
  const txGuarded = items.filter((item) => item.safetyClass.includes('tx-capable')).length;
  const rxSafe = items.filter((item) => item.safetyClass === 'rx-safe').length;
  const top = rows[0];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Advertised', value: items.length },
          { label: 'Configurable', value: items.filter((item) => item.userConfigurable).length },
          { label: 'Planned APIs', value: plannedApiCount },
          { label: 'RX-safe', value: rxSafe },
          { label: 'TX Guarded', value: txGuarded },
          { label: 'Top Target', value: top?.item.title },
        ]}
      />
      {rows.map((row, index) => (
        <div
          key={row.item.id}
          style={{
            padding: '9px 10px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'baseline',
              justifyContent: 'space-between',
              gap: 8,
              flexWrap: 'wrap',
            }}
          >
            <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>
              {index + 1}. {row.item.title}
            </span>
            <span
              className="mono"
              style={{
                fontSize: 10,
                color: row.item.safetyClass.includes('tx-capable') ? 'var(--tx)' : 'var(--accent)',
              }}
            >
              {row.readiness} / score {row.priority}
            </span>
          </div>
          <FieldGrid
            fields={[
              { label: 'Category', value: row.item.category },
              { label: 'Safety', value: row.item.safetyClass },
              { label: 'Configurable', value: boolLabel(row.item.userConfigurable) },
              { label: 'Planned', value: row.plannedControls.join(', ') || '-' },
            ]}
          />
          <div style={{ marginTop: 7, fontSize: 11, lineHeight: 1.35, color: 'var(--fg-2)' }}>
            {row.nextStep}
          </div>
          <div className="mono" style={{ marginTop: 6, fontSize: 10, color: 'var(--fg-3)' }}>
            live controls: {row.liveControls.join(', ') || '-'}
          </div>
          <div className="mono" style={{ marginTop: 3, fontSize: 10, color: 'var(--fg-3)' }}>
            telemetry: {row.item.telemetryPaths.slice(0, 5).join(', ')}
            {row.item.telemetryPaths.length > 5 ? ` +${row.item.telemetryPaths.length - 5} more` : ''}
          </div>
        </div>
      ))}
    </div>
  );
}

function FeatureSurfaceRows({ items }: { items: HardwareFeatureSurfaceDto[] }) {
  if (items.length === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
        This backend has not advertised hardware feature surfaces yet.
      </div>
    );
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      {items.map((item) => (
        <div
          key={item.id}
          style={{
            padding: '8px 10px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'baseline',
              justifyContent: 'space-between',
              gap: 8,
              flexWrap: 'wrap',
            }}
          >
            <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>
              {item.title}
            </span>
            <span className="mono" style={{ fontSize: 10, color: 'var(--accent)' }}>
              {item.implementationStatus}
            </span>
          </div>
          <FieldGrid
            fields={[
              { label: 'ID', value: item.id },
              { label: 'Category', value: item.category },
              { label: 'Configurable', value: boolLabel(item.userConfigurable) },
              { label: 'Safety', value: item.safetyClass },
              { label: 'Source', value: item.source },
            ]}
          />
          <div style={{ marginTop: 7, fontSize: 11, lineHeight: 1.35, color: 'var(--fg-2)' }}>
            {item.notes}
          </div>
          <div className="mono" style={{ marginTop: 6, fontSize: 10, color: 'var(--fg-3)' }}>
            telemetry: {item.telemetryPaths.join(', ') || '-'}
          </div>
          <div className="mono" style={{ marginTop: 3, fontSize: 10, color: 'var(--fg-3)' }}>
            controls: {item.candidateControls.join(', ') || '-'}
          </div>
        </div>
      ))}
    </div>
  );
}

const tableStyle: CSSProperties = {
  width: '100%',
  borderCollapse: 'collapse',
  fontSize: 11,
};

const thStyle: CSSProperties = {
  padding: '5px 6px',
  textAlign: 'left',
  borderBottom: '1px solid var(--panel-border)',
  color: 'var(--fg-3)',
  fontSize: 9,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
};

const tdStyle: CSSProperties = {
  padding: '5px 6px',
  borderBottom: '1px solid var(--panel-border)',
  verticalAlign: 'top',
};

function changedBits(bitChangeCounts: number[]): string {
  const bits = bitChangeCounts
    .map((count, bit) => (count > 0 ? bit : null))
    .filter((bit): bit is number => bit !== null);
  return bits.length === 0 ? '-' : bits.join(',');
}

function P2ByteMap({ map }: { map: HardwareByteStreamMapDto }) {
  if (map.samples === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
        No P2 hi-priority packets captured in this mapping window.
      </div>
    );
  }
  const wordRows = map.words.filter((w) => w.known || w.changeCount > 0);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <FieldGrid
        fields={[
          { label: 'Stream', value: map.stream },
          { label: 'Samples', value: map.samples },
          { label: 'Length', value: map.length },
          { label: 'Changed Bytes', value: map.changedByteCount },
          { label: 'Changed Words', value: map.changedWordCount },
          { label: 'Started', value: time(map.startedUtc) },
          { label: 'Updated', value: time(map.lastUpdatedUtc) },
          { label: 'Last Payload', value: map.lastHex },
        ]}
      />
      <div style={{ overflowX: 'auto' }}>
        <table style={tableStyle}>
          <thead>
            <tr>
              <th style={thStyle}>Byte</th>
              <th style={thStyle}>Known / Candidate</th>
              <th style={thStyle}>Last</th>
              <th style={thStyle}>Range</th>
              <th style={thStyle}>Delta</th>
              <th style={thStyle}>Bits</th>
            </tr>
          </thead>
          <tbody>
            {map.bytes.map((b) => (
              <tr key={b.offset}>
                <td style={tdStyle} className="mono">{b.hexOffset}</td>
                <td style={tdStyle}>{b.known ?? '-'}</td>
                <td style={tdStyle} className="mono">{hex(b.last, 2)}</td>
                <td style={tdStyle} className="mono">{hex(b.min, 2)}..{hex(b.max, 2)}</td>
                <td style={tdStyle} className="mono">
                  {b.changeCount} / {b.changedMaskHex}
                </td>
                <td style={tdStyle} className="mono">{changedBits(b.bitChangeCounts)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {wordRows.length > 0 && (
        <div style={{ overflowX: 'auto' }}>
          <table style={tableStyle}>
            <thead>
              <tr>
                <th style={thStyle}>Word</th>
                <th style={thStyle}>Known / Candidate</th>
                <th style={thStyle}>Last</th>
                <th style={thStyle}>Range</th>
                <th style={thStyle}>Delta</th>
              </tr>
            </thead>
            <tbody>
              {wordRows.map((w) => (
                <tr key={w.offset}>
                  <td style={tdStyle} className="mono">{w.hexOffset}</td>
                  <td style={tdStyle}>{w.known ?? 'changed overlapping u16'}</td>
                  <td style={tdStyle} className="mono">{adc(w.last)}</td>
                  <td style={tdStyle} className="mono">{adc(w.min)}..{adc(w.max)}</td>
                  <td style={tdStyle} className="mono">{w.changeCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function P1Map({ map }: { map: HardwareP1MapDto }) {
  if (map.samples === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
        No Protocol 1 C&amp;C telemetry captured in this mapping window.
      </div>
    );
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      <FieldGrid
        fields={[
          { label: 'Stream', value: map.stream },
          { label: 'Samples', value: map.samples },
          { label: 'Addresses', value: map.addresses.length },
          { label: 'Started', value: time(map.startedUtc) },
          { label: 'Updated', value: time(map.lastUpdatedUtc) },
        ]}
      />
      <div style={{ overflowX: 'auto' }}>
        <table style={tableStyle}>
          <thead>
            <tr>
              <th style={thStyle}>C0</th>
              <th style={thStyle}>AIN0</th>
              <th style={thStyle}>AIN1</th>
              <th style={thStyle}>Last</th>
              <th style={thStyle}>Range</th>
              <th style={thStyle}>Delta</th>
            </tr>
          </thead>
          <tbody>
            {map.addresses.map((a) => (
              <tr key={a.hexAddress}>
                <td style={tdStyle} className="mono">{a.hexAddress}</td>
                <td style={tdStyle}>{a.knownAin0 ?? '-'}</td>
                <td style={tdStyle}>{a.knownAin1 ?? '-'}</td>
                <td style={tdStyle} className="mono">{adc(a.lastAin0)} / {adc(a.lastAin1)}</td>
                <td style={tdStyle} className="mono">
                  {adc(a.minAin0)}..{adc(a.maxAin0)} / {adc(a.minAin1)}..{adc(a.maxAin1)}
                </td>
                <td style={tdStyle} className="mono">
                  {a.ain0ChangeCount} / {a.ain1ChangeCount}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {map.rawGap && (
        <div style={{ fontSize: 11, lineHeight: 1.35, color: 'var(--fg-3)' }}>
          {map.rawGap}
        </div>
      )}
    </div>
  );
}

function signed(v: number | null | undefined): string {
  if (v === null || v === undefined) return '-';
  return v > 0 ? `+${v}` : String(v);
}

function bitList(bits: number[]): string {
  return bits.length === 0 ? '-' : bits.join(',');
}

function MarkerTimeline({ markers }: { markers: HardwareMappingMarkerDto[] }) {
  if (markers.length === 0) {
    return (
      <div style={{ fontSize: 12, color: 'var(--fg-2)' }}>
        No capture markers in this mapping window.
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {markers.map((marker) => {
        const delta = marker.sincePrevious;
        const byteRows = delta.p2ChangedBytes.slice(0, 14);
        const wordRows = delta.p2ChangedWords.slice(0, 10);
        const p1Rows = delta.p1ChangedAddresses.slice(0, 8);
        return (
          <div
            key={marker.id}
            style={{
              padding: '8px 10px',
              background: 'var(--bg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm)',
            }}
          >
            <div
              style={{
                display: 'flex',
                alignItems: 'baseline',
                gap: 8,
                justifyContent: 'space-between',
                flexWrap: 'wrap',
              }}
            >
              <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--fg-0)' }}>
                {marker.label}
              </span>
              <span className="mono" style={{ fontSize: 10, color: 'var(--fg-3)' }}>
                #{marker.id} {time(marker.createdUtc)}
              </span>
            </div>
            {marker.notes && (
              <div style={{ marginTop: 4, fontSize: 11, color: 'var(--fg-2)' }}>
                {marker.notes}
              </div>
            )}
            <FieldGrid
              fields={[
                { label: 'Previous', value: delta.baseline ? 'baseline' : delta.previousLabel },
                { label: 'Protocol', value: marker.activeProtocol },
                { label: 'P2 Samples', value: delta.p2SampleDelta },
                { label: 'P1 Samples', value: delta.p1SampleDelta },
                { label: 'P2 Bytes', value: delta.p2ChangedBytes.length },
                { label: 'P2 Words', value: delta.p2ChangedWords.length },
                { label: 'P1 C0', value: delta.p1ChangedAddresses.length },
                { label: 'Endpoint', value: marker.endpoint },
              ]}
            />
            {byteRows.length > 0 && (
              <div style={{ overflowX: 'auto', marginTop: 8 }}>
                <table style={tableStyle}>
                  <thead>
                    <tr>
                      <th style={thStyle}>Byte</th>
                      <th style={thStyle}>Known / Candidate</th>
                      <th style={thStyle}>Prev</th>
                      <th style={thStyle}>Now</th>
                      <th style={thStyle}>XOR</th>
                      <th style={thStyle}>Interval</th>
                      <th style={thStyle}>Bits</th>
                    </tr>
                  </thead>
                  <tbody>
                    {byteRows.map((b) => (
                      <tr key={b.offset}>
                        <td style={tdStyle} className="mono">{b.hexOffset}</td>
                        <td style={tdStyle}>{b.known ?? '-'}</td>
                        <td style={tdStyle} className="mono">{b.previousHex}</td>
                        <td style={tdStyle} className="mono">{b.currentHex}</td>
                        <td style={tdStyle} className="mono">{b.xorMaskHex}</td>
                        <td style={tdStyle} className="mono">{b.intervalChangeCount}</td>
                        <td style={tdStyle} className="mono">{bitList(b.intervalChangedBits)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {wordRows.length > 0 && (
              <div style={{ overflowX: 'auto', marginTop: 8 }}>
                <table style={tableStyle}>
                  <thead>
                    <tr>
                      <th style={thStyle}>Word</th>
                      <th style={thStyle}>Known / Candidate</th>
                      <th style={thStyle}>Prev</th>
                      <th style={thStyle}>Now</th>
                      <th style={thStyle}>Delta</th>
                      <th style={thStyle}>Interval</th>
                    </tr>
                  </thead>
                  <tbody>
                    {wordRows.map((w) => (
                      <tr key={w.offset}>
                        <td style={tdStyle} className="mono">{w.hexOffset}</td>
                        <td style={tdStyle}>{w.known ?? 'changed overlapping u16'}</td>
                        <td style={tdStyle} className="mono">{w.previousHex}</td>
                        <td style={tdStyle} className="mono">{w.currentHex}</td>
                        <td style={tdStyle} className="mono">{signed(w.valueDelta)}</td>
                        <td style={tdStyle} className="mono">{w.intervalChangeCount}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {p1Rows.length > 0 && (
              <div style={{ overflowX: 'auto', marginTop: 8 }}>
                <table style={tableStyle}>
                  <thead>
                    <tr>
                      <th style={thStyle}>C0</th>
                      <th style={thStyle}>AIN0</th>
                      <th style={thStyle}>AIN1</th>
                      <th style={thStyle}>Delta</th>
                      <th style={thStyle}>Interval</th>
                    </tr>
                  </thead>
                  <tbody>
                    {p1Rows.map((p) => (
                      <tr key={p.c0Address}>
                        <td style={tdStyle} className="mono">{p.hexAddress}</td>
                        <td style={tdStyle}>{p.knownAin0 ?? '-'}</td>
                        <td style={tdStyle}>{p.knownAin1 ?? '-'}</td>
                        <td style={tdStyle} className="mono">
                          {signed(p.ain0Delta)} / {signed(p.ain1Delta)}
                        </td>
                        <td style={tdStyle} className="mono">
                          {p.ain0ChangeCountDelta} / {p.ain1ChangeCountDelta}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

export function HardwareDiagnosticsPanel() {
  const [diag, setDiag] = useState<HardwareDiagnosticsDto | null>(null);
  const [radios, setRadios] = useState<RadioInfoDto[] | null>(null);
  const [keying, setKeying] = useState<HardwareKeyingStatusDto | null>(null);
  const [supplyAlarms, setSupplyAlarms] = useState<RadioSupplyAlarmsDto | null>(null);
  const [networkProfile, setNetworkProfile] = useState<RadioNetworkProfileDto | null>(null);
  const [txDiagnostics, setTxDiagnostics] = useState<TxDiagnosticsDto | null>(null);
  const [smartNrCondition, setSmartNrCondition] = useState<SmartNrConditionDto | null>(null);
  const [userIoLabels, setUserIoLabels] = useState<UserIoLabelsDto | null>(null);
  const [userIoActions, setUserIoActions] = useState<UserIoActionsDto | null>(null);
  const [digInDiagnostics, setDigInDiagnostics] = useState<RadioDigInDiagnosticsDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [inventoryError, setInventoryError] = useState<string | null>(null);
  const [endpointError, setEndpointError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [scanning, setScanning] = useState(false);
  const [endpointBusy, setEndpointBusy] = useState(false);
  const [resettingMap, setResettingMap] = useState(false);
  const [marking, setMarking] = useState(false);
  const [markerLabel, setMarkerLabel] = useState('');
  const [markerNotes, setMarkerNotes] = useState('');

  const load = useCallback(async (signal?: AbortSignal) => {
    setBusy(true);
    try {
      const next = await fetchHardwareDiagnostics(signal);
      setDiag(next);
      setError(null);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setBusy(false);
    }
  }, []);

  const scan = useCallback(async (signal?: AbortSignal) => {
    setScanning(true);
    try {
      const next = await fetchRadios(signal);
      setRadios(next);
      setInventoryError(null);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') {
        setInventoryError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setScanning(false);
    }
  }, []);

  const loadEndpointDiagnostics = useCallback(async (signal?: AbortSignal) => {
    setEndpointBusy(true);
    try {
      const [nextKeying, nextSupply, nextNetwork, nextTx, nextSmartNr, nextLabels, nextActions, nextDigIn] =
        await Promise.allSettled([
          fetchHardwareKeyingStatus(signal),
          fetchRadioSupplyAlarms(signal),
          fetchRadioNetworkProfile(signal),
          fetchTxDiagnostics(signal),
          fetchSmartNrCondition(signal),
          fetchUserIoLabels(signal),
          fetchUserIoActions(signal),
          fetchRadioDigInDiagnostics(signal),
        ]);

      if (nextKeying.status === 'fulfilled') setKeying(nextKeying.value);
      if (nextSupply.status === 'fulfilled') setSupplyAlarms(nextSupply.value);
      if (nextNetwork.status === 'fulfilled') setNetworkProfile(nextNetwork.value);
      if (nextTx.status === 'fulfilled') setTxDiagnostics(nextTx.value);
      if (nextSmartNr.status === 'fulfilled') setSmartNrCondition(nextSmartNr.value);
      if (nextLabels.status === 'fulfilled') setUserIoLabels(nextLabels.value);
      if (nextActions.status === 'fulfilled') setUserIoActions(nextActions.value);
      if (nextDigIn.status === 'fulfilled') setDigInDiagnostics(nextDigIn.value);

      const failures = [nextKeying, nextSupply, nextNetwork, nextTx, nextSmartNr, nextLabels, nextActions, nextDigIn]
        .filter(
          (result): result is PromiseRejectedResult =>
            result.status === 'rejected' &&
            (result.reason as DOMException).name !== 'AbortError'
        )
        .map((result) =>
          result.reason instanceof Error ? result.reason.message : String(result.reason)
        );
      setEndpointError(failures.length > 0 ? failures.join(' / ') : null);
    } finally {
      setEndpointBusy(false);
    }
  }, []);

  const resetMap = useCallback(async (signal?: AbortSignal) => {
    setResettingMap(true);
    try {
      const next = await resetHardwareDiagnosticsMap(signal);
      setDiag(next);
      setError(null);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setResettingMap(false);
    }
  }, []);

  const createMarker = useCallback(async (label?: string, signal?: AbortSignal) => {
    const effectiveLabel = (label ?? markerLabel).trim();
    setMarking(true);
    try {
      const next = await createHardwareDiagnosticsMarker(
        effectiveLabel || `Marker ${(diag?.mapping.markers.length ?? 0) + 1}`,
        markerNotes.trim() || undefined,
        signal,
      );
      setDiag(next);
      setMarkerLabel('');
      setMarkerNotes('');
      setError(null);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setMarking(false);
    }
  }, [diag?.mapping.markers.length, markerLabel, markerNotes]);

  useEffect(() => {
    const ac = new AbortController();
    void load(ac.signal);
    const id = window.setInterval(() => void load(), 1000);
    return () => {
      ac.abort();
      window.clearInterval(id);
    };
  }, [load]);

  useEffect(() => {
    const ac = new AbortController();
    void scan(ac.signal);
    const id = window.setInterval(() => void scan(), INVENTORY_INTERVAL_MS);
    return () => {
      ac.abort();
      window.clearInterval(id);
    };
  }, [scan]);

  useEffect(() => {
    const ac = new AbortController();
    void loadEndpointDiagnostics(ac.signal);
    const id = window.setInterval(() => void loadEndpointDiagnostics(), 2000);
    return () => {
      ac.abort();
      window.clearInterval(id);
    };
  }, [loadEndpointDiagnostics]);

  const caps = diag?.capabilities;
  const connectionFields: Field[] = [
    { label: 'Status', value: diag?.connectionStatus },
    { label: 'Protocol', value: diag?.activeProtocol },
    { label: 'Endpoint', value: diag?.endpoint },
    { label: 'Detected', value: diag?.connectedBoard },
    { label: 'Effective', value: diag?.effectiveBoard },
    { label: '0x0A Variant', value: diag?.orionMkIIVariant },
    { label: 'Sample Rate', value: diag?.sampleRate },
    { label: 'Mode', value: diag?.mode },
  ];

  const capabilityFields: Field[] = [
    { label: 'RX ADCs', value: caps?.rxAdcCount },
    { label: 'MKII BPF', value: boolLabel(caps?.mkiiBpf) },
    { label: 'ADC Supply', value: caps ? `${caps.adcSupplyMv} mV` : null },
    { label: 'L/R Swap', value: boolLabel(caps?.lrAudioSwap) },
    { label: 'Volts', value: boolLabel(caps?.hasVolts) },
    { label: 'Amps', value: boolLabel(caps?.hasAmps) },
    { label: 'Audio Amp', value: boolLabel(caps?.hasAudioAmplifier) },
    { label: 'RX2 Step ATT', value: boolLabel(caps?.hasSteppedAttenuationRx2) },
    { label: 'Path Illustrator', value: boolLabel(caps?.supportsPathIllustrator) },
    { label: 'Max Watts', value: caps?.maxPowerWatts },
    { label: 'Max RX Rate', value: caps ? `${caps.maxRxSampleRateHz / 1000} kHz` : null },
    { label: 'HL2 Options', value: boolLabel(caps?.hasHl2OptionalToggles) },
    { label: 'Anvelina DX OC', value: boolLabel(caps?.supportsAnvelinaDxOc) },
  ];

  const dsp = diag?.dsp;
  const display = dsp?.display;
  const rxDsp = dsp?.rxDsp;
  const rxMeters = dsp?.rxMeters;
  const audio = dsp?.audio;
  const dspFields: Field[] = [
    { label: 'Engine', value: dsp?.engineKind },
    { label: 'Runtime', value: dsp?.engine },
    { label: 'Readiness', value: dsp?.readiness },
    { label: 'WDSP Active', value: boolLabel(dsp?.wdspActive) },
    { label: 'WDSP Native', value: boolLabel(dsp?.wdspNativeLoadable) },
    { label: 'NR2 Post2', value: boolLabel(dsp?.wdspEmnrPost2Available) },
    { label: 'NR4 SBNR', value: boolLabel(dsp?.wdspNr4SbnrAvailable) },
    { label: 'NR4 Readiness', value: dsp?.nr4Readiness },
    { label: 'NR Requested', value: dsp?.requestedNrMode },
    { label: 'NR Effective', value: dsp?.effectiveNrMode },
    { label: 'Wisdom', value: dsp?.wdspWisdomPhase },
    { label: 'Channel', value: dsp?.channelId },
    { label: 'DSP Rate', value: dsp?.sampleRateHz },
    { label: 'Display Width', value: dsp?.displayWidth },
    { label: 'Tick Hz', value: dsp?.tickRateHz },
    { label: 'Audio Out', value: dsp?.audioOutputRateHz },
    { label: 'TX Block', value: dsp?.txBlockSamples },
    { label: 'TX IQ Out', value: dsp?.txOutputSamples },
    { label: 'TX Monitor', value: boolLabel(dsp?.txMonitorRequested) },
    { label: 'RX Sink', value: boolLabel(dsp?.rxSinkAttached) },
    { label: 'Audio Sinks', value: dsp?.audioSinkCount },
    { label: 'Monitor Backlog', value: dsp?.monitorBacklogSamples },
    { label: 'Wisdom Status', value: dsp?.wdspWisdomStatus },
  ];
  const audioFields: Field[] = [
    { label: 'Audio Status', value: audio?.status },
    { label: 'Source', value: audio?.source },
    { label: 'Fresh', value: boolLabel(audio?.fresh) },
    { label: 'Stale', value: boolLabel(audio?.stale) },
    { label: 'Age', value: age(audio?.ageMs) },
    { label: 'Frames', value: count(audio?.framesBroadcast) },
    { label: 'Last Seq', value: audio?.lastSeq },
    { label: 'Rate', value: audio?.sampleRateHz },
    { label: 'Samples', value: audio?.sampleCount },
    { label: 'RMS', value: db(audio?.rmsDbfs) },
    { label: 'Peak', value: db(audio?.peakDbfs) },
    { label: 'RMS Linear', value: audio?.rmsLinear },
    { label: 'Peak Linear', value: audio?.peakLinear },
    { label: 'TX Monitor', value: boolLabel(audio?.txMonitorRequested) },
    { label: 'Squelch', value: boolLabel(audio?.squelchEnabled) },
    { label: 'SQL Open', value: boolLabel(audio?.squelchOpen) },
    { label: 'SQL Tail', value: boolLabel(audio?.squelchTailActive) },
    { label: 'SQL Gain', value: audio?.squelchGateGain },
    { label: 'Monitor Backlog', value: audio?.monitorBacklogSamples },
    { label: 'Audio Sinks', value: audio?.audioSinkCount },
  ];
  const rxDspFields: Field[] = [
    { label: 'RX DSP Status', value: rxDsp?.status },
    { label: 'Mode', value: rxDsp?.mode },
    { label: 'Filter Low', value: hz(rxDsp?.filterLowHz) },
    { label: 'Filter High', value: hz(rxDsp?.filterHighHz) },
    { label: 'Filter Preset', value: rxDsp?.filterPresetName },
    { label: 'AGC Mode', value: rxDsp?.agcMode },
    { label: 'AGC Top', value: db(rxDsp?.agcTopDb) },
    { label: 'Auto AGC', value: boolLabel(rxDsp?.autoAgcEnabled) },
    { label: 'AGC Offset', value: db(rxDsp?.agcOffsetDb) },
    { label: 'AGC Effective', value: db(rxDsp?.effectiveAgcTopDb) },
    { label: 'Squelch', value: boolLabel(rxDsp?.squelchEnabled) },
    { label: 'SQL Mode', value: rxDsp ? (rxDsp.squelchAdaptive ? 'Adaptive' : 'Fixed') : null },
    { label: 'SQL Level', value: rxDsp?.squelchLevel },
    { label: 'NR Requested', value: rxDsp?.requestedNrMode },
    { label: 'NR Effective', value: rxDsp?.effectiveNrMode },
    { label: 'ANF', value: boolLabel(rxDsp?.anfEnabled) },
    { label: 'SNB', value: boolLabel(rxDsp?.snbEnabled) },
    { label: 'NBP Notches', value: boolLabel(rxDsp?.nbpNotchesEnabled) },
    { label: 'NBP Effective', value: boolLabel(rxDsp?.effectiveNbpNotchesRun) },
    { label: 'NB Mode', value: rxDsp?.nbMode },
    { label: 'NB Threshold', value: rxDsp?.nbThreshold },
    { label: 'Manual Notches', value: rxDsp?.manualNotchCount },
    { label: 'Active Notches', value: rxDsp?.activeManualNotchCount },
    { label: 'NR4 Ready', value: rxDsp?.nr4Readiness },
    { label: 'NR Applied', value: boolLabel(rxDsp?.appliedNrMatchesRequested) },
    { label: 'AGC Applied', value: boolLabel(rxDsp?.appliedAgcMatchesRequested) },
    { label: 'SQL Applied', value: boolLabel(rxDsp?.appliedSquelchMatchesRequested) },
    {
      label: 'Active Features',
      value: rxDsp ? (rxDsp.activeFeatures.length > 0 ? rxDsp.activeFeatures.join(', ') : 'none') : null,
    },
    {
      label: 'DSP Reasons',
      value: rxDsp ? (rxDsp.qualityReasons.length > 0 ? rxDsp.qualityReasons.join(', ') : 'none') : null,
    },
  ];
  const rxMeterFields: Field[] = [
    { label: 'RXA Meter Status', value: rxMeters?.status },
    { label: 'Meter Source', value: rxMeters?.source },
    { label: 'Fresh', value: boolLabel(rxMeters?.fresh) },
    { label: 'Stale', value: boolLabel(rxMeters?.stale) },
    { label: 'Age', value: age(rxMeters?.ageMs) },
    { label: 'Channel', value: rxMeters?.channelId },
    { label: 'S-Meter', value: db(rxMeters?.rxDbm) },
    { label: 'Signal Pk', value: db(rxMeters?.signalPkDbm) },
    { label: 'Signal Av', value: db(rxMeters?.signalAvDbm) },
    { label: 'ADC Pk', value: db(rxMeters?.adcPkDbfs) },
    { label: 'ADC Av', value: db(rxMeters?.adcAvDbfs) },
    { label: 'ADC Headroom', value: db(rxMeters?.adcHeadroomDb) },
    { label: 'AGC Gain', value: db(rxMeters?.agcGainDb) },
    { label: 'AGC Env Pk', value: db(rxMeters?.agcEnvPkDbm) },
    { label: 'AGC Env Av', value: db(rxMeters?.agcEnvAvDbm) },
    { label: 'Signal Usable', value: boolLabel(rxMeters?.signalUsable) },
    { label: 'ADC Usable', value: boolLabel(rxMeters?.adcUsable) },
    { label: 'AGC Env Usable', value: boolLabel(rxMeters?.agcEnvelopeUsable) },
  ];
  const displayFields: Field[] = [
    { label: 'Display Status', value: display?.status },
    { label: 'Clients', value: display?.clientCount },
    { label: 'Frames', value: display?.framesBroadcast },
    { label: 'Last Seq', value: display?.lastSeq },
    { label: 'Frame Age', value: age(display?.lastFrameAgeMs) },
    { label: 'Keyed', value: boolLabel(display?.keyed) },
    { label: 'PS Monitor', value: boolLabel(display?.psMonitorRequested) },
    { label: 'PS Feedback', value: boolLabel(display?.psFeedbackCorrecting) },
    { label: 'Width', value: display?.width },
    { label: 'Center', value: hz(display?.centerHz) },
    { label: 'Hz / Pixel', value: display?.hzPerPixel },
    { label: 'Pan Valid', value: boolLabel(display?.panValid) },
    { label: 'Pan Source', value: display?.panSource },
    { label: 'Pan Age', value: age(display?.pan.ageMs) },
    { label: 'Pan Bins', value: display?.pan.validBins },
    { label: 'Pan Min', value: db(display?.pan.minDb) },
    { label: 'Pan Max', value: db(display?.pan.maxDb) },
    { label: 'Pan Mean', value: db(display?.pan.meanDb) },
    { label: 'Pan Range', value: db(display?.pan.dynamicRangeDb) },
    { label: 'WF Valid', value: boolLabel(display?.waterfallValid) },
    { label: 'WF Source', value: display?.waterfallSource },
    { label: 'WF Age', value: age(display?.waterfall.ageMs) },
    { label: 'WF Bins', value: display?.waterfall.validBins },
    { label: 'WF Min', value: db(display?.waterfall.minDb) },
    { label: 'WF Max', value: db(display?.waterfall.maxDb) },
    { label: 'WF Mean', value: db(display?.waterfall.meanDb) },
    { label: 'WF Range', value: db(display?.waterfall.dynamicRangeDb) },
  ];

  const scene = diag?.frontendDspScene;
  const sceneFields: Field[] = [
    { label: 'Available', value: boolLabel(scene?.available) },
    { label: 'Status', value: scene?.status },
    { label: 'Fresh', value: boolLabel(scene?.fresh) },
    { label: 'Stale', value: boolLabel(scene?.stale) },
    { label: 'Age', value: age(scene?.ageMs) },
    { label: 'Source Age', value: age(scene?.sourceAgeMs) },
    { label: 'Source Skew', value: age(scene?.sourceClockSkewMs) },
    { label: 'Diagnostic Action', value: scene?.diagnosticRecommendation },
    { label: 'Client', value: scene?.sourceClientId },
    { label: 'Mode', value: scene?.mode },
    { label: 'Signal Profile', value: scene?.signalProfile },
    { label: 'Signal Reason', value: scene?.signalReason },
    { label: 'Smart NR', value: scene?.smartNrProfile },
    { label: 'Smart NR Reason', value: scene?.smartNrReason },
    { label: 'Smart NR Action', value: scene?.smartNrRecommendation },
    { label: 'RX Hold', value: boolLabel(scene?.smartNrHeldByRxChain) },
    { label: 'RX Chain', value: scene?.smartNrRxChainLabel },
    { label: 'Max SNR', value: db(scene?.maxSnrDb) },
    { label: 'Coherent Max', value: db(scene?.coherentMaxSnrDb) },
    { label: 'Occupied', value: pct(scene?.occupiedPct) },
    { label: 'Coherent Occ', value: pct(scene?.coherentOccupiedPct) },
    { label: 'Impulsive', value: pct(scene?.impulsivePct) },
    { label: 'Peaks', value: scene?.peakCount },
    { label: 'Coherent Peaks', value: scene?.coherentPeakCount },
    { label: 'Subthreshold Ridge', value: boolLabel(scene?.coherentSubthresholdSignal) },
    { label: 'Source Updated', value: time(scene?.sourceAtUtc) },
    { label: 'Updated', value: time(scene?.atUtc) },
  ];
  const nrCondition = smartNrCondition;
  const rxChain = nrCondition?.rxChain;
  const rxChainFields: Field[] = [
    { label: 'NR Condition', value: nrCondition?.status },
    { label: 'NR Profile', value: nrCondition?.profile },
    { label: 'NR Runtime', value: nrCondition ? `${nrCondition.requestedNrMode} -> ${nrCondition.effectiveNrMode}` : null },
    { label: 'NR Expected', value: nrCondition?.expectedNrMode },
    { label: 'NR Aligned', value: boolLabel(nrCondition?.runtimeAligned) },
    { label: 'NR Align Status', value: nrCondition?.runtimeAlignmentStatus },
    { label: 'NR Align Action', value: nrCondition?.runtimeAlignmentRecommendation },
    { label: 'RX Source', value: rxChain?.source },
    { label: 'Auto AGC', value: boolLabel(rxChain?.autoAgcEnabled) },
    { label: 'AGC Mode', value: rxChain?.agcMode },
    { label: 'AGC Top', value: db(rxChain?.agcTopDb) },
    { label: 'AGC Offset', value: db(rxChain?.agcOffsetDb) },
    { label: 'AGC Effective', value: db(rxChain?.effectiveAgcTopDb) },
    { label: 'Auto ATT', value: boolLabel(rxChain?.autoAttEnabled) },
    { label: 'ADC Protect', value: boolLabel(rxChain?.adcProtectionEnabled) },
    { label: 'ATT Base', value: db(rxChain?.attenDb) },
    { label: 'ATT Offset', value: db(rxChain?.attOffsetDb) },
    { label: 'ATT Effective', value: db(rxChain?.effectiveAttenDb) },
    { label: 'ADC Warning', value: boolLabel(rxChain?.adcOverloadWarning) },
    { label: 'Overload Level', value: rxChain?.adcOverloadLevel },
    { label: 'Overload Bits', value: hex(rxChain?.lastOverloadBits, 2) },
    { label: 'ADC0 Max', value: adc(rxChain?.adc0MaxMagnitude) },
    { label: 'ADC1 Max', value: adc(rxChain?.adc1MaxMagnitude) },
    { label: 'ADC0 Trip', value: adc(rxChain?.adc0MaxMagnitudeAtOverload) },
    { label: 'ADC1 Trip', value: adc(rxChain?.adc1MaxMagnitudeAtOverload) },
    { label: 'ADC Updated', value: time(rxChain?.lastAdcTelemetryUtc) },
    { label: 'Squelch', value: boolLabel(rxChain?.squelchEnabled) },
    { label: 'Squelch Mode', value: rxChain ? (rxChain.squelchAdaptive ? 'Adaptive' : 'Fixed') : null },
    { label: 'Squelch Level', value: rxChain?.squelchLevel },
    { label: 'Preamp', value: boolLabel(rxChain?.preampOn) },
    { label: 'NR Generated', value: time(nrCondition?.generatedUtc) },
  ];

  const p1 = diag?.p1;
  const p1Fields: Field[] = [
    { label: 'Packets', value: p1?.packets },
    { label: 'Updated', value: time(p1?.lastUpdatedUtc) },
    { label: 'Last C0', value: hex(p1?.lastC0Address, 2) },
    { label: 'AIN0', value: adc(p1?.lastAin0) },
    { label: 'AIN1', value: adc(p1?.lastAin1) },
    { label: 'Exciter', value: adc(p1?.exciterAdc) },
    { label: 'Forward', value: adc(p1?.fwdAdc) },
    { label: 'Reverse', value: adc(p1?.revAdc) },
    { label: 'User ADC0', value: adc(p1?.userAdc0) },
    { label: 'User ADC1', value: adc(p1?.userAdc1) },
    { label: 'Supply', value: adc(p1?.supplyVoltsAdc) },
    { label: 'ADC Overload', value: hex(p1?.adcOverloadBits, 2) },
    { label: 'HW PTT', value: boolLabel(p1?.hardwarePtt) },
    { label: 'CW Key', value: boolLabel(p1?.cwKeyDown) },
  ];

  const p2 = diag?.p2;
  const p2Fields: Field[] = [
    { label: 'Packets', value: p2?.packets },
    { label: 'Updated', value: time(p2?.lastUpdatedUtc) },
    { label: 'PTT In', value: boolLabel(p2?.pttIn) },
    { label: 'Dot', value: boolLabel(p2?.dotIn) },
    { label: 'Dash', value: boolLabel(p2?.dashIn) },
    { label: 'PLL Lock', value: boolLabel(p2?.pllLocked) },
    { label: 'Sidetone', value: boolLabel(p2?.sidetoneActive) },
    { label: 'ADC Overload', value: hex(p2?.adcOverloadBits, 2) },
    { label: 'Exciter', value: adc(p2?.exciterAdc) },
    { label: 'Forward', value: adc(p2?.fwdAdc) },
    { label: 'Reverse', value: adc(p2?.revAdc) },
    { label: 'ADC0 Max', value: adc(p2?.adc0MaxMagnitude) },
    { label: 'ADC1 Max', value: adc(p2?.adc1MaxMagnitude) },
    { label: 'ADC0 Over Max', value: adc(p2?.adc0MaxMagnitudeAtOverload) },
    { label: 'ADC1 Over Max', value: adc(p2?.adc1MaxMagnitudeAtOverload) },
    { label: 'Supply', value: adc(p2?.supplyVoltsAdc) },
    { label: 'User ADC0', value: adc(p2?.userAdc0) },
    { label: 'User ADC1', value: adc(p2?.userAdc1) },
    { label: 'User ADC2', value: adc(p2?.userAdc2) },
    { label: 'User ADC3', value: adc(p2?.userAdc3) },
    { label: 'User DIN', value: hex(p2?.userDigitalIn, 2) },
    { label: 'HW LEDs', value: hex(p2?.hardwareLeds, 4) },
  ];

  return (
    <div className="ps-shell">
      <div className="ps-card">
        <h4>
          Hardware Diagnostics
          <span className="ps-card-hint">
            {busy ? 'refreshing' : diag ? `updated ${time(diag.generatedUtc)}` : 'loading'}
          </span>
        </h4>
        <div style={{ display: 'flex', gap: 8, marginBottom: 10 }}>
          <button type="button" className="btn sm" onClick={() => void load()} disabled={busy}>
            REFRESH
          </button>
          {error && (
            <span style={{ alignSelf: 'center', fontSize: 11, color: 'var(--tx)' }}>
              {error}
            </span>
          )}
        </div>
        <FieldGrid fields={connectionFields} />
      </div>

      <div className="ps-card">
        <h4>
          Network Inventory
          <span className="ps-card-hint">
            {scanning ? 'scanning' : `${radios?.length ?? 0} discovered`}
          </span>
        </h4>
        <div style={{ display: 'flex', gap: 8, marginBottom: 10 }}>
          <button type="button" className="btn sm" onClick={() => void scan()} disabled={scanning}>
            SCAN
          </button>
          {inventoryError && (
            <span style={{ alignSelf: 'center', fontSize: 11, color: 'var(--tx)' }}>
              {inventoryError}
            </span>
          )}
        </div>
        <RadioInventory radios={radios} />
      </div>

      <div className="ps-card">
        <h4>
          Board Capabilities
          <span className="ps-card-hint">Thetis-derived static map</span>
        </h4>
        <FieldGrid fields={capabilityFields} />
      </div>

      <div className="ps-card">
        <h4>
          Receiver Topology
          <span className="ps-card-hint">G2 manual ADC / DDC / filter-bank audit</span>
        </h4>
        <ReceiverTopologyDiagnostics diag={diag} />
      </div>

      <div className="ps-card">
        <h4>
          DSP Runtime
          <span className="ps-card-hint">engine, timing, TX path, WDSP readiness</span>
        </h4>
        <FieldGrid fields={dspFields} />
        <div style={{ marginTop: 10 }}>
          <FieldGrid fields={audioFields} />
        </div>
        <div style={{ marginTop: 10 }}>
          <FieldGrid fields={rxDspFields} />
        </div>
        <div style={{ marginTop: 10 }}>
          <FieldGrid fields={rxMeterFields} />
        </div>
        <div style={{ marginTop: 10 }}>
          <FieldGrid fields={displayFields} />
        </div>
        <DiagnosticRecommendation text={rxDsp?.diagnosticRecommendation} />
        <DiagnosticRecommendation text={rxMeters?.diagnosticRecommendation} />
        <DiagnosticRecommendation text={audio?.diagnosticRecommendation} />
        <DiagnosticRecommendation text={display?.diagnosticRecommendation} />
      </div>

      <div className="ps-card">
        <h4>
          DSP Scene Intelligence
          <span className="ps-card-hint">frontend Signal Intelligence and Smart NR evidence</span>
        </h4>
        <FieldGrid fields={sceneFields} />
        <div style={{ marginTop: 10 }}>
          <FieldGrid fields={rxChainFields} />
        </div>
        <DiagnosticRecommendation text={nrCondition?.diagnosticRecommendation} />
      </div>

      <div className="ps-card">
        <h4>
          Hardware Keying &amp; External PTT
          <span className="ps-card-hint">
            {endpointBusy ? 'refreshing' : 'decoded /api/cw/hardware-keying'}
          </span>
        </h4>
        {endpointError && (
          <div style={{ marginBottom: 8, fontSize: 11, color: 'var(--tx)' }}>
            {endpointError}
          </div>
        )}
        <HardwareKeyingDiagnostics status={keying} />
      </div>

      <div className="ps-card">
        <h4>
          G2 Dig In TX Disable
          <span className="ps-card-hint">rear jack tip CW key / ring active-low inhibit</span>
        </h4>
        <DigInDiagnostics diag={digInDiagnostics ?? diag?.digIn ?? null} />
      </div>

      <div className="ps-card">
        <h4>
          PA Supply Alarms
          <span className="ps-card-hint">scaled P1/P2 supply telemetry</span>
        </h4>
        <SupplyAlarmDiagnostics alarms={supplyAlarms} />
      </div>

      <div className="ps-card">
        <h4>
          Radio Network Profile
          <span className="ps-card-hint">transport health / frame loss / hi-priority flow</span>
        </h4>
        <NetworkProfileDiagnostics profile={networkProfile} />
      </div>

      <div className="ps-card">
        <h4>
          TX Egress Diagnostics
          <span className="ps-card-hint">mic ingest / P1 ring / P2 DUC packets</span>
        </h4>
        <TxEgressDiagnostics diag={txDiagnostics} />
      </div>

      <div className="ps-card">
        <h4>
          PureSignal Feedback Health
          <span className="ps-card-hint">internal coupler / RF Bypass / calcc window</span>
        </h4>
        <PureSignalFeedbackDiagnostics diag={diag?.pureSignal} />
      </div>

      <div className="ps-card">
        <h4>
          User I/O Labels &amp; Actions
          <span className="ps-card-hint">P2 analog/digital lines and guarded bindings</span>
        </h4>
        <UserIoDiagnostics labels={userIoLabels} actions={userIoActions} />
      </div>

      <div className="ps-card">
        <h4>
          Next-Level Opportunities
          <span className="ps-card-hint">ranked from live diagnostics</span>
        </h4>
        <HardwareOpportunityMatrix items={diag?.featureSurfaces ?? []} />
      </div>

      <div className="ps-card">
        <h4>
          Protocol 1 Live
          <span className="ps-card-hint">C&amp;C echo telemetry</span>
        </h4>
        <FieldGrid fields={p1Fields} />
      </div>

      <div className="ps-card">
        <h4>
          Protocol 2 Live
          <span className="ps-card-hint">hi-priority status</span>
        </h4>
        <FieldGrid fields={p2Fields} />
      </div>

      <div className="ps-card">
        <h4>
          Mapping Capture
          <span className="ps-card-hint">
            raw status byte map · {diag?.mapping.markers.length ?? 0} markers
          </span>
        </h4>
        <div style={{ display: 'flex', gap: 8, marginBottom: 10, flexWrap: 'wrap' }}>
          <button type="button" className="btn sm" onClick={() => void resetMap()} disabled={resettingMap}>
            RESET MAP
          </button>
          <button type="button" className="btn sm" onClick={() => void load()} disabled={busy}>
            REFRESH
          </button>
          <button type="button" className="btn sm" onClick={() => void createMarker('Baseline')} disabled={marking}>
            BASELINE
          </button>
          <button type="button" className="btn sm" onClick={() => void createMarker('After RX Action')} disabled={marking}>
            RX ACTION
          </button>
          <button type="button" className="btn sm" onClick={() => void createMarker()} disabled={marking}>
            MARK
          </button>
        </div>
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'minmax(160px, 0.7fr) minmax(220px, 1.3fr)',
            gap: 8,
            marginBottom: 12,
          }}
        >
          <input
            value={markerLabel}
            onChange={(event) => setMarkerLabel(event.target.value)}
            placeholder="Marker label"
            maxLength={80}
            style={{
              minWidth: 0,
              padding: '6px 8px',
              background: 'var(--bg-0)',
              color: 'var(--fg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm)',
              fontSize: 12,
            }}
          />
          <input
            value={markerNotes}
            onChange={(event) => setMarkerNotes(event.target.value)}
            placeholder="Notes"
            maxLength={220}
            style={{
              minWidth: 0,
              padding: '6px 8px',
              background: 'var(--bg-0)',
              color: 'var(--fg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm)',
              fontSize: 12,
            }}
          />
        </div>
        {diag?.mapping && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <MarkerTimeline markers={diag.mapping.markers} />
            <P2ByteMap map={diag.mapping.p2HiPriority} />
            <P1Map map={diag.mapping.p1} />
          </div>
        )}
      </div>

      <div className="ps-card">
        <h4>
          Reference Map
          <span className="ps-card-hint">Thetis parity anchors</span>
        </h4>
        <MappingRows items={diag?.referenceMap ?? []} />
      </div>

      <div className="ps-card">
        <h4>
          Feature Surfaces
          <span className="ps-card-hint">API hooks for future controls</span>
        </h4>
        <FeatureSurfaceRows items={diag?.featureSurfaces ?? []} />
      </div>

      <div className="ps-card">
        <h4>
          Settings Candidates
          <span className="ps-card-hint">configurable follow-up surfaces</span>
        </h4>
        <MappingRows items={diag?.candidateSettings ?? []} />
      </div>
    </div>
  );
}
