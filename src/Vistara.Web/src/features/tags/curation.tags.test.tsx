import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { VistaraApiError } from '../../api/generated/client';
import type { Tag } from '../../api/generated/models';
import { TagsView } from './TagsView';

const tag: Tag = {
  id: 'tag-1',
  name: 'Landscape',
  color: '#82d4bb',
  assetCount: 4,
  version: 2,
};

describe('curation tags', () => {
  it('provides flat tag filters and an accessible editor', async () => {
    const user = userEvent.setup();
    const onFilterChange = vi.fn();
    const client = {
      listTags: vi.fn().mockResolvedValue({ data: { items: [tag] } }),
      createTag: vi.fn(),
      updateTag: vi.fn().mockResolvedValue({
        data: { ...tag, name: 'Coast', version: 3 },
        etag: '"v3"',
      }),
      deleteTag: vi.fn(),
    };

    render(
      <TagsView
        client={client}
        selectedTagIds={[]}
        onFilterChange={onFilterChange}
      />,
    );
    await screen.findByRole('heading', { name: 'Tags' });

    await user.click(
      screen.getByRole('checkbox', { name: 'Filter by Landscape' }),
    );
    expect(onFilterChange).toHaveBeenCalledWith(['tag-1']);

    await user.click(screen.getByRole('button', { name: 'Edit Landscape' }));
    const name = screen.getByLabelText('Tag name');
    await user.clear(name);
    await user.type(name, 'Coast');
    await user.click(screen.getByRole('button', { name: 'Save tag' }));

    expect(client.updateTag).toHaveBeenCalledWith(
      'tag-1',
      { name: 'Coast', color: '#82d4bb' },
      expect.objectContaining({ ifMatch: '"v2"' }),
    );
    expect(
      await screen.findByRole('button', { name: 'Edit Coast' }),
    ).toBeInTheDocument();
  });

  it('reconciles an optimistic tag edit after a conflict', async () => {
    const user = userEvent.setup();
    let rejectEdit: ((reason: unknown) => void) | undefined;
    const client = {
      listTags: vi
        .fn()
        .mockResolvedValueOnce({ data: { items: [tag] } })
        .mockResolvedValueOnce({
          data: { items: [{ ...tag, name: 'Team tag', version: 3 }] },
        }),
      createTag: vi.fn(),
      updateTag: vi.fn(
        () =>
          new Promise<never>((_resolve, reject) => {
            rejectEdit = reject;
          }),
      ),
      deleteTag: vi.fn(),
    };

    render(<TagsView client={client} />);
    await screen.findByRole('button', { name: 'Edit Landscape' });
    await user.click(screen.getByRole('button', { name: 'Edit Landscape' }));
    await user.clear(screen.getByLabelText('Tag name'));
    await user.type(screen.getByLabelText('Tag name'), 'Mine');
    await user.click(screen.getByRole('button', { name: 'Save tag' }));

    expect(screen.getByRole('button', { name: 'Edit Mine' })).toBeInTheDocument();
    rejectEdit?.(conflict());

    expect(
      await screen.findByRole('button', { name: 'Edit Team tag' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Tags changed elsewhere. The latest list was restored.',
    );
  });
});

function conflict() {
  return new VistaraApiError(412, {
    type: 'about:blank',
    title: 'Precondition failed',
    status: 412,
    code: 'version_conflict',
    errors: {},
  });
}
