import { describe, it, expect } from 'vitest';
import { deriveScalars } from './registration';

// Cross-language agreement: the browser's Argon2id (noble) MUST derive the same
// (w0, w1) as the C# server's (Konscious), or a password set on the radio won't
// unlock from the phone. This vector is pinned identically in the C# test
// Spake2PlusRegistrationTests.DeriveScalars_MatchesCrossLanguageVector.
describe('SPAKE2+ registration — Argon2id cross-language agreement', () => {
  it('derives the same (w0, w1) as the C# server', () => {
    const salt = new Uint8Array(16);
    for (let i = 0; i < 16; i++) salt[i] = i;

    const { w0, w1 } = deriveScalars('zeus-cross-vector', salt, {
      iterations: 1,
      memoryKib: 8,
      parallelism: 1,
    });

    expect(w0.toString(16).padStart(64, '0')).toBe(
      '5038ef6d5486f2dd9321ec16a6d4e0d91379299bc14650db32c78dfd58e43818',
    );
    expect(w1.toString(16).padStart(64, '0')).toBe(
      '5df0c3ed05e170c4296186f19639c7c79de43fddb39f7745d6a3c61241b5175f',
    );
  });
});
