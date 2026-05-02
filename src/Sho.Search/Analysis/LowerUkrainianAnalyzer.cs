using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Uk;
using Lucene.Net.Util;

namespace Sho.Search.Analysis;

/// <summary>
/// <see cref="UkrainianMorfologikAnalyzer"/> emits lemmas with the casing stored
/// in the bundled UA dictionary — proper-noun lemmas come back Title-Cased
/// ("Ніколаєнко", "Юрій"). That breaks Lucene's case-sensitive multi-term
/// queries (Wildcard / Fuzzy) when the user types lowercase.
///
/// <see cref="UkrainianMorfologikAnalyzer"/> is sealed, so we wrap it via
/// <see cref="AnalyzerWrapper"/> and append a final <see cref="LowerCaseFilter"/>.
/// Now every indexed term — and every query token analyzed through it — is
/// canonical lowercase, keeping the three branches of the hybrid query
/// (lemma TermQuery, substring WildcardQuery, FuzzyQuery) consistent on the
/// same casing.
/// </summary>
internal sealed class LowerUkrainianAnalyzer : AnalyzerWrapper
{
    private readonly LuceneVersion _lv;
    private readonly UkrainianMorfologikAnalyzer _inner;

    public LowerUkrainianAnalyzer(LuceneVersion lv)
        : base(PER_FIELD_REUSE_STRATEGY)
    {
        _lv = lv;
        _inner = new UkrainianMorfologikAnalyzer(lv);
    }

    protected override Analyzer GetWrappedAnalyzer(string fieldName) => _inner;

    protected override TokenStreamComponents WrapComponents(string fieldName, TokenStreamComponents components)
    {
        return new TokenStreamComponents(
            components.Tokenizer,
            new LowerCaseFilter(_lv, components.TokenStream));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
