export const HISTORY_CATEGORY_PARAM = "hc";
export const STREAMING_HISTORY_CATEGORIES = ["streaming-movie", "streaming-series"] as const;

export function normalizeHistoryCategory(value: string | null | undefined): string | null {
    const normalized = value?.trim();
    return normalized ? normalized : null;
}

export function matchesHistoryCategory(category: string, selectedCategory: string | null): boolean {
    return selectedCategory === null || category === selectedCategory;
}

export function historyCategorySearchParams(
    current: URLSearchParams,
    selectedCategory: string | null,
): URLSearchParams {
    const next = new URLSearchParams(current);
    const normalized = normalizeHistoryCategory(selectedCategory);
    if (normalized === null) next.delete(HISTORY_CATEGORY_PARAM);
    else next.set(HISTORY_CATEGORY_PARAM, normalized);
    next.set("hp", "1");
    return next;
}

export function historyCategoryOptions(categories: string[], selectedCategory: string | null): string[] {
    const uniqueCategories = categories
        .map(category => category.trim())
        .filter((category, index, all) => category.length > 0 && all.indexOf(category) === index);
    if (selectedCategory !== null && !uniqueCategories.includes(selectedCategory)) {
        return [selectedCategory, ...uniqueCategories];
    }
    return uniqueCategories;
}
