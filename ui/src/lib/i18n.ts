// Formats the templates of strings.json - the same rules as
// RappyRuns.Core/I18n/Template.cs, checked against the same golden file:
//   {n}          argument n
//   {n?text}     text only when argument n is non-null
//   {n#one|many} "one" when argument n is 1, else "many"

export type Arg = string | number | null | undefined;

export function format(template: string, args: readonly Arg[] = []): string {
  const state = { i: 0 };
  const out = render(template, state, args, true, false);
  return out;
}

function render(t: string, state: { i: number }, args: readonly Arg[], emit: boolean, stopAtClose: boolean): string {
  let out = '';
  while (state.i < t.length) {
    const c = t[state.i];
    if (c === '}' && stopAtClose) {
      state.i++;
      return out;
    }
    if (c !== '{') {
      if (emit) out += c;
      state.i++;
      continue;
    }
    state.i++;
    const start = state.i;
    while (state.i < t.length && t[state.i] >= '0' && t[state.i] <= '9') state.i++;
    if (state.i === start || state.i >= t.length) throw new Error(`Bad placeholder in "${t}"`);
    const n = Number(t.slice(start, state.i));
    if (n >= args.length) throw new Error(`Missing argument ${n} for "${t}"`);
    const arg = args[n];
    const kind = t[state.i++];
    if (kind === '}') {
      if (emit) out += arg == null ? '' : String(arg);
    } else if (kind === '?') {
      const body = render(t, state, args, emit && arg != null, true);
      out += body;
    } else if (kind === '#') {
      const close = t.indexOf('}', state.i);
      if (close < 0) throw new Error(`Unclosed plural in "${t}"`);
      const forms = t.slice(state.i, close).split('|');
      if (forms.length !== 2) throw new Error(`Plural needs two forms in "${t}"`);
      if (emit) out += arg === 1 ? forms[0] : forms[1];
      state.i = close + 1;
    } else {
      throw new Error(`Bad placeholder in "${t}"`);
    }
  }
  if (stopAtClose) throw new Error(`Unclosed conditional in "${t}"`);
  return out;
}

/** A tr() bound to one language's templates. A missing key is a bug and throws. */
export function translator(strings: Record<string, string>) {
  return (key: string, ...args: Arg[]): string => {
    const template = strings[key];
    if (template === undefined) throw new Error(`No UI string for ${key}`);
    return format(template, args);
  };
}
