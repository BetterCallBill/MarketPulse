// @vitest-environment node
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const componentsDir = fileURLToPath(new URL('../components', import.meta.url));

function cssFilesUnder(directory: string): string[] {
  return readdirSync(directory).flatMap((entry) => {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) return cssFilesUnder(path);
    return path.endsWith('.css') ? [path] : [];
  });
}

/** Primitive tokens and raw hex are the two ways a component can escape the semantic layer. */
const FORBIDDEN = /--mp-(grey|green|red|blue)-|#[0-9a-fA-F]{3,8}\b/;

describe('component stylesheets', () => {
  const files = cssFilesUnder(componentsDir);

  it('finds stylesheets to check', () => {
    expect(files.length).toBeGreaterThan(0);
  });

  it.each(files)('%s references only semantic tokens', (file) => {
    const offending = readFileSync(file, 'utf8')
      .split('\n')
      .filter((line) => FORBIDDEN.test(line));

    expect(offending).toEqual([]);
  });
});
