// Every string of every language must format exactly like the Lisp
// client's FORMAT did (golden file written by desktop/tools/export-i18n.lisp).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { format } from './i18n.ts';

const root = new URL('../../../', import.meta.url);
const strings = JSON.parse(readFileSync(new URL('src/RappyRuns.Core/I18n/strings.json', root), 'utf8')) as Record<string, { en: string; ja: string }>;
const golden = JSON.parse(readFileSync(new URL('tests/RappyRuns.Tests/golden/i18n-format.json', root), 'utf8')) as {
  key: string;
  lang: 'en' | 'ja';
  args: (string | number | null)[];
  expected: string;
}[];

test('golden FORMAT output', () => {
  assert.ok(golden.length > 0);
  for (const g of golden) {
    assert.equal(format(strings[g.key][g.lang], g.args), g.expected, `${g.key} (${g.lang}) ${JSON.stringify(g.args)}`);
  }
});

test('every key has both languages', () => {
  for (const [key, entry] of Object.entries(strings)) {
    assert.ok(entry.en.length > 0 && entry.ja.length > 0, key);
  }
});
