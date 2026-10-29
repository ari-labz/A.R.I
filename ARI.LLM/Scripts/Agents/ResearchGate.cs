namespace ARI.LLM;

internal static class ResearchGate
{
    // Real research takes many searches and many pages, so these are a safety net against a spiral, not a budget. They were 2, 3 and 8,
    // which cut a careful answer off after a handful of lookups.
    internal const int SEARCH_SOFT_LIMIT = 8;    // pause and self-assess
    internal const int SEARCH_HARD_LIMIT = 20;   // search_web withdrawn for the rest of the turn
    internal const int READ_LIMIT        = 50;   // fetch_page withdrawn for the rest of the turn
}
