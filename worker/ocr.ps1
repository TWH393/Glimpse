param(
    [Parameter(Mandatory = $true)][string]$ImagePath,
    [string]$Languages = 'eng'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$stream = $null
$bitmap = $null
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
    [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and
        $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    function Await-WinRt($Operation, [Type]$ResultType) {
        $task = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
        $task.GetAwaiter().GetResult()
    }
    $available = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages)
    if ($available.Count -eq 0) { throw 'No Windows OCR language packs are installed. Install a Windows language OCR capability or configure Tesseract.' }
    $aliases = @{ eng = 'en'; chi_sim = 'zh-Hans'; chi_tra = 'zh-Hant'; jpn = 'ja'; kor = 'ko'; fra = 'fr'; deu = 'de'; spa = 'es' }
    $codes = @($Languages.Split('+') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    # A Windows OCR engine uses one recognizer; prefer Chinese for Chinese/Latin text.
    $preferred = $codes | Where-Object { $_ -eq 'chi_sim' -or $_ -eq 'chi_tra' } | Select-Object -First 1
    if (!$preferred) { $preferred = $codes | Select-Object -First 1 }
    if (!$preferred -or $preferred -eq 'auto') {
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    } else {
        $tag = if ($aliases.ContainsKey($preferred)) { $aliases[$preferred] } else { $preferred }
        $language = $available | Where-Object {
            $_.LanguageTag -eq $tag -or $_.LanguageTag.StartsWith($tag + '-', [StringComparison]::OrdinalIgnoreCase) -or
            ($tag -eq 'zh-Hans' -and $_.LanguageTag -in @('zh-CN', 'zh-SG')) -or
            ($tag -eq 'zh-Hant' -and $_.LanguageTag -in @('zh-TW', 'zh-HK', 'zh-MO'))
        } | Select-Object -First 1
        if (!$language) { throw "Windows OCR language '$preferred' is not installed. Available: $($available.LanguageTag -join ', '). Install its Windows OCR capability or configure Tesseract." }
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
    }
    if (!$engine) { throw 'Unable to create a Windows OCR recognizer. Check installed Windows OCR languages.' }
    $file = Await-WinRt ([Windows.Storage.StorageFile]::GetFileFromPathAsync($ImagePath)) ([Windows.Storage.StorageFile])
    $stream = Await-WinRt ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await-WinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await-WinRt ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied)) ([Windows.Graphics.Imaging.SoftwareBitmap])
    if ($bitmap.PixelWidth -gt [Windows.Media.Ocr.OcrEngine]::MaxImageDimension -or $bitmap.PixelHeight -gt [Windows.Media.Ocr.OcrEngine]::MaxImageDimension) {
        throw "Selected image is too large for Windows OCR (maximum dimension: $([Windows.Media.Ocr.OcrEngine]::MaxImageDimension)). Select a smaller region."
    }
    $result = Await-WinRt ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    @{ ok = $true; text = ($result.Lines.Text -join "`n"); language = $engine.RecognizerLanguage.LanguageTag } | ConvertTo-Json -Compress
} catch {
    @{ ok = $false; error = $_.Exception.Message } | ConvertTo-Json -Compress
} finally {
    if ($bitmap) { $bitmap.Dispose() }
    if ($stream) { $stream.Dispose() }
}
