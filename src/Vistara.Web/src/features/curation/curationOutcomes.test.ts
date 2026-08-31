import { describe, expect, it } from 'vitest';
import {
  describeOutcome,
  summarizeCuration,
  type CurationItemResult,
} from './curationOutcomes';

function result(
  id: string,
  outcome: CurationItemResult['outcome'],
): CurationItemResult {
  return { id, title: `Image ${id}`, outcome };
}

describe('curation outcomes', () => {
  it('reports a plain success for a whole selection', () => {
    const summary = summarizeCuration('Added to Summer', [
      result('a', 'updated'),
      result('b', 'updated'),
    ]);

    expect(summary.message).toBe('Added to Summer: 2 images.');
    expect(summary.tone).toBe('success');
  });

  it('uses the singular for one image', () => {
    expect(
      summarizeCuration('Favorited', [result('a', 'updated')]).message,
    ).toBe('Favorited: 1 image.');
  });

  it('separates the images that needed another try', () => {
    const summary = summarizeCuration('Moved to trash', [
      result('a', 'updated'),
      result('b', 'conflict'),
      result('c', 'notFound'),
      result('d', 'failed'),
      result('e', 'unchanged'),
    ]);

    expect(summary.message).toBe(
      'Moved to trash: 1 image. 1 image was already in that state. ' +
        '1 image changed elsewhere and was left alone. ' +
        '1 image is no longer available. 1 image could not be updated.',
    );
    expect(summary.tone).toBe('warning');
  });

  it('reports a failure when nothing succeeded', () => {
    const summary = summarizeCuration('Favorited', [
      result('a', 'failed'),
      result('b', 'conflict'),
    ]);

    expect(summary.message).toBe(
      'Favorited: no images. 1 image changed elsewhere and was left alone. ' +
        '1 image could not be updated.',
    );
    expect(summary.tone).toBe('danger');
  });

  it('uses the plural for several images', () => {
    expect(
      summarizeCuration('Moved to trash', [
        result('a', 'updated'),
        result('b', 'conflict'),
        result('c', 'conflict'),
      ]).message,
    ).toBe(
      'Moved to trash: 1 image. 2 images changed elsewhere and were left alone.',
    );
  });

  it('counts a recovered stale version as a success', () => {
    const summary = summarizeCuration('Tagged', [result('a', 'refreshed')]);

    expect(summary.message).toBe(
      'Tagged: 1 image. 1 image was refreshed before being applied.',
    );
    expect(summary.tone).toBe('success');
  });

  it('reports work the API accepted but has not finished', () => {
    const summary = summarizeCuration('Added to favorites', [
      result('a', 'queued'),
      result('b', 'queued'),
    ]);

    expect(summary.message).toBe('Added to favorites: 2 images queued.');
    expect(summary.tone).toBe('success');
  });

  it('separates queued work from work that was refused', () => {
    const summary = summarizeCuration('Added to favorites', [
      result('a', 'queued'),
      result('b', 'failed'),
    ]);

    expect(summary.message).toBe(
      'Added to favorites: 1 image queued. 1 image could not be updated.',
    );
    expect(summary.tone).toBe('warning');
  });

  it('describes each outcome for the per-image list', () => {
    expect(describeOutcome('updated')).toBe('Done');
    expect(describeOutcome('refreshed')).toBe('Done after a refresh');
    expect(describeOutcome('queued')).toBe('Queued');
    expect(describeOutcome('unchanged')).toBe('Already in that state');
    expect(describeOutcome('conflict')).toBe('Changed elsewhere');
    expect(describeOutcome('notFound')).toBe('No longer available');
    expect(describeOutcome('failed')).toBe('Could not be updated');
  });
});
