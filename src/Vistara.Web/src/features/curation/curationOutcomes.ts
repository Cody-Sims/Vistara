/** What happened to one asset in a curation action. */
export type CurationOutcome =
  | 'updated'
  | 'refreshed'
  | 'unchanged'
  | 'conflict'
  | 'notFound'
  | 'failed';

export interface CurationItemResult {
  readonly id: string;
  readonly title: string;
  readonly outcome: CurationOutcome;
}

export interface CurationSummary {
  readonly message: string;
  readonly tone: 'success' | 'warning' | 'danger';
}

const succeeded: ReadonlySet<CurationOutcome> = new Set([
  'updated',
  'refreshed',
]);

export function describeOutcome(outcome: CurationOutcome): string {
  switch (outcome) {
    case 'updated':
      return 'Done';
    case 'refreshed':
      return 'Done after a refresh';
    case 'unchanged':
      return 'Already in that state';
    case 'conflict':
      return 'Changed elsewhere';
    case 'notFound':
      return 'No longer available';
    default:
      return 'Could not be updated';
  }
}

function images(count: number): string {
  return count === 1 ? '1 image' : `${count} images`;
}

function were(count: number): string {
  return count === 1 ? 'was' : 'were';
}

/**
 * One sentence a screen reader can read in a live region, followed by the
 * exceptions. Every asset is accounted for, so a partly applied selection is
 * never reported as a plain success.
 */
export function summarizeCuration(
  action: string,
  results: readonly CurationItemResult[],
): CurationSummary {
  const count = (outcome: CurationOutcome) =>
    results.filter((result) => result.outcome === outcome).length;
  const done = results.filter((result) => succeeded.has(result.outcome)).length;
  const parts = [`${action}: ${done === 0 ? 'no images' : images(done)}.`];

  const refreshed = count('refreshed');
  if (refreshed > 0) {
    parts.push(
      `${images(refreshed)} ${were(refreshed)} refreshed before being applied.`,
    );
  }

  const unchanged = count('unchanged');
  if (unchanged > 0) {
    parts.push(`${images(unchanged)} ${were(unchanged)} already in that state.`);
  }

  const conflict = count('conflict');
  if (conflict > 0) {
    parts.push(
      `${images(conflict)} changed elsewhere and ${were(conflict)} left alone.`,
    );
  }

  const missing = count('notFound');
  if (missing > 0) {
    parts.push(
      `${images(missing)} ${missing === 1 ? 'is' : 'are'} no longer available.`,
    );
  }

  const failed = count('failed');
  if (failed > 0) {
    parts.push(`${images(failed)} could not be updated.`);
  }

  return {
    message: parts.join(' '),
    tone:
      done === 0 && results.length > 0
        ? 'danger'
        : conflict + missing + failed + unchanged > 0
          ? 'warning'
          : 'success',
  };
}
