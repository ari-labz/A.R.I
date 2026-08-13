namespace ARI.LLM;

internal static class ResearchGate
{
    internal const int SEARCH_SOFT_LIMIT = 2;   // pause and self-assess
    internal const int SEARCH_HARD_LIMIT = 3;   // search_web withdrawn for the rest of the turn
    internal const int READ_LIMIT        = 8;   // fetch_page withdrawn for the rest of the turn
}
