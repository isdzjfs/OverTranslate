using System.Drawing;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services;

/// <summary>OCR results for one locked screenshot crop and its recognition settings.</summary>
internal sealed class CaptureOcrReuse
{
    private Bitmap? _bitmap;
    private string? _sourceLanguage;
    private bool _vertical;
    private CaptureLayoutMode _layoutMode;
    private List<OcrTextBlock> _blocks = [];

    public bool TryGet(Bitmap bitmap, string sourceLanguage, bool vertical,
        CaptureLayoutMode layoutMode, out List<OcrTextBlock> blocks)
    {
        if (ReferenceEquals(_bitmap, bitmap)
            && _blocks.Count > 0
            && string.Equals(_sourceLanguage, sourceLanguage, StringComparison.Ordinal)
            && _vertical == vertical
            && _layoutMode == layoutMode)
        {
            blocks = _blocks;
            return true;
        }

        blocks = [];
        return false;
    }

    public void Store(Bitmap bitmap, string sourceLanguage, bool vertical,
        CaptureLayoutMode layoutMode, List<OcrTextBlock> blocks)
    {
        _bitmap = bitmap;
        _sourceLanguage = sourceLanguage;
        _vertical = vertical;
        _layoutMode = layoutMode;
        _blocks = blocks;
    }

    public void Clear()
    {
        _bitmap = null;
        _blocks = [];
    }
}
