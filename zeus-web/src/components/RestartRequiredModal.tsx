// SPDX-License-Identifier: GPL-2.0-or-later
//
// RestartRequiredModal — shared "you need to restart Zeus" dialog
// surfaced after any successful plugin install (Audio Suite bundle
// download, single-plugin install from the Plugins panel registry,
// or future install-by-URL flows).
//
// Plugin endpoints + AssemblyLoadContexts only register at backend
// startup, so a restart is the only way to bring fresh installs into
// the live process. The modal is operator-acknowledged — we never
// auto-restart Zeus.
//
// Counts (installed / skipped / errors) are optional because the
// single-plugin install path doesn't have a meaningful skipped/errors
// figure. When the bundle-install path passes them, they render as a
// monospaced status block; otherwise the modal is just the headline +
// the explanatory text.

interface RestartRequiredModalProps {
    installed?: number;
    skipped?: number;
    errors?: number;
    /**
     * Optional single-plugin display name (e.g. "Noise Gate v0.1.0").
     * Used when the bundle counts aren't meaningful (registry-card
     * install of one plugin); rendered in the body instead of the
     * counts block.
     */
    pluginDisplayName?: string;
    onClose: () => void;
}

export function RestartRequiredModal({
    installed,
    skipped,
    errors,
    pluginDisplayName,
    onClose,
}: RestartRequiredModalProps) {
    const hasCounts = typeof installed === 'number';

    return (
        <div
            className="modal-backdrop"
            style={{
                position: 'fixed',
                inset: 0,
                background: 'rgba(0, 0, 0, 0.7)',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                zIndex: 10000,
            }}
            onClick={onClose}
        >
            <div
                onClick={(e) => e.stopPropagation()}
                role="dialog"
                aria-modal="true"
                aria-labelledby="restart-required-title"
                style={{
                    maxWidth: 460,
                    width: '90vw',
                    padding: 20,
                    background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
                    border: '1px solid var(--line-1)',
                    borderRadius: 8,
                    color: 'var(--fg-0)',
                    fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
                    boxShadow: 'inset 0 2px 0 var(--power), inset 0 3px 8px rgba(255, 201, 58, 0.08), 0 10px 30px rgba(0, 0, 0, 0.55)',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 14,
                }}
            >
                <h2
                    id="restart-required-title"
                    style={{
                        margin: 0,
                        fontSize: 14,
                        fontWeight: 600,
                        letterSpacing: 1.5,
                        textTransform: 'uppercase',
                        color: 'var(--fg-0)',
                        textShadow: '0 0 8px rgba(255, 201, 58, 0.18)',
                    }}
                >
                    Restart required
                </h2>

                <p style={{ margin: 0, fontSize: 13, color: 'var(--fg-1)', lineHeight: 1.5 }}>
                    Please shut down Zeus and restart for the new plugins to take effect.
                </p>

                {hasCounts ? (
                    <div style={{
                        padding: '8px 12px',
                        background: 'var(--bg-1)',
                        border: '1px solid var(--line-1)',
                        borderRadius: 4,
                        fontSize: 11,
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        color: 'var(--fg-2)',
                        display: 'flex',
                        flexDirection: 'column',
                        gap: 4,
                    }}>
                        <div>installed: <span style={{ color: 'var(--fg-0)' }}>{installed}</span></div>
                        {skipped !== undefined && skipped > 0 && (
                            <div>already present: <span style={{ color: 'var(--fg-0)' }}>{skipped}</span></div>
                        )}
                        {errors !== undefined && errors > 0 && (
                            <div style={{ color: 'var(--tx)' }}>failed: {errors}</div>
                        )}
                    </div>
                ) : pluginDisplayName ? (
                    <div style={{
                        padding: '8px 12px',
                        background: 'var(--bg-1)',
                        border: '1px solid var(--line-1)',
                        borderRadius: 4,
                        fontSize: 11,
                        fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                        color: 'var(--fg-0)',
                    }}>
                        installed: {pluginDisplayName}
                    </div>
                ) : null}

                <p style={{ margin: 0, fontSize: 11, color: 'var(--fg-3)', lineHeight: 1.4 }}>
                    New plugin endpoints and audio-chain blocks only register at backend
                    startup. After Zeus relaunches you'll see the new plugins in the chain
                    and in the Plugins panel above.
                </p>

                <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
                    <button
                        type="button"
                        className="btn sm active"
                        autoFocus
                        onClick={onClose}
                    >
                        OK
                    </button>
                </div>
            </div>
        </div>
    );
}
