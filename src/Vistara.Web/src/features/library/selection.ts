export type SelectionState =
  | {
      mode: 'explicit';
      selectedIds: ReadonlySet<string>;
      anchorId?: string;
    }
  | {
      mode: 'all';
      totalCount: number;
      excludedIds: ReadonlySet<string>;
      anchorId?: string;
    };

export function createSelectionState(): SelectionState {
  return { mode: 'explicit', selectedIds: new Set() };
}

export function isSelected(selection: SelectionState, assetId: string) {
  return selection.mode === 'all'
    ? !selection.excludedIds.has(assetId)
    : selection.selectedIds.has(assetId);
}

export function selectionCount(selection: SelectionState) {
  return selection.mode === 'all'
    ? Math.max(0, selection.totalCount - selection.excludedIds.size)
    : selection.selectedIds.size;
}

export function toggleSelection(
  selection: SelectionState,
  assetId: string,
): SelectionState {
  if (selection.mode === 'all') {
    const excludedIds = new Set(selection.excludedIds);
    if (excludedIds.has(assetId)) excludedIds.delete(assetId);
    else excludedIds.add(assetId);
    return { ...selection, excludedIds, anchorId: assetId };
  }

  const selectedIds = new Set(selection.selectedIds);
  if (selectedIds.has(assetId)) selectedIds.delete(assetId);
  else selectedIds.add(assetId);
  return { ...selection, selectedIds, anchorId: assetId };
}

export function selectRange(
  selection: SelectionState,
  orderedIds: readonly string[],
  assetId: string,
): SelectionState {
  if (selection.mode === 'all' || !selection.anchorId) {
    return toggleSelection(selection, assetId);
  }

  const anchorIndex = orderedIds.indexOf(selection.anchorId);
  const assetIndex = orderedIds.indexOf(assetId);
  if (anchorIndex < 0 || assetIndex < 0) {
    return toggleSelection(selection, assetId);
  }

  const selectedIds = new Set(selection.selectedIds);
  const start = Math.min(anchorIndex, assetIndex);
  const end = Math.max(anchorIndex, assetIndex);
  for (const id of orderedIds.slice(start, end + 1)) selectedIds.add(id);

  return { mode: 'explicit', selectedIds, anchorId: selection.anchorId };
}

export function selectVisible(
  selection: SelectionState,
  visibleIds: readonly string[],
): SelectionState {
  if (selection.mode === 'all') {
    const excludedIds = new Set(selection.excludedIds);
    visibleIds.forEach((id) => excludedIds.delete(id));
    return { ...selection, excludedIds };
  }

  return {
    ...selection,
    selectedIds: new Set([...selection.selectedIds, ...visibleIds]),
  };
}

export function selectAllResults(
  selection: SelectionState,
  totalCount: number,
): SelectionState {
  return {
    mode: 'all',
    totalCount: Math.max(0, totalCount),
    excludedIds: new Set(),
    anchorId: selection.anchorId,
  };
}
