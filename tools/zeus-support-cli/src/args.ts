/** Tiny wrapper over node:util parseArgs with shared option groups. */

import { parseArgs, type ParseArgsConfig } from 'node:util';

export type Options = NonNullable<ParseArgsConfig['options']>;

/** Options every command accepts (broker selection + token). */
export const commonOptions: Options = {
  broker: { type: 'string' },
  token: { type: 'string' },
  help: { type: 'boolean', short: 'h' },
  json: { type: 'boolean' },
};

/**
 * Parse argv for one subcommand. `args` should already have the program name +
 * subcommand stripped. Throws on unknown options (strict) for clear errors.
 */
export function parse(args: string[], options: Options): {
  values: Record<string, string | boolean | undefined>;
  positionals: string[];
} {
  const { values, positionals } = parseArgs({
    args,
    options,
    allowPositionals: true,
    strict: true,
  });
  return { values: values as Record<string, string | boolean | undefined>, positionals };
}
