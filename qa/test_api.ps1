$baseUrl = "http://localhost:5000"
$root = Resolve-Path "$PSScriptRoot\.."
$dataDir = "$root\qa-data"
$tmpDir = "$root\qa-tmp"

Write-Host "1. Проверка Health..." -ForegroundColor Cyan
curl.exe -s -o nul -w "   Health: %{http_code}`n" "$baseUrl/v1/health"

Write-Host "2. Проверка эндпоинтов по спецификации..." -ForegroundColor Cyan
curl.exe -s -o nul -w "   /v1/compset: %{http_code}`n" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$dataDir\api_text.txt" "$baseUrl/v1/compset"
curl.exe -s -o nul -w "   /v1/deccomp: %{http_code}`n" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$tmpDir\api.arc" "$baseUrl/v1/deccomp"

Write-Host "3. Round-trip через /v1/compress..." -ForegroundColor Cyan
curl.exe -s -o "$tmpDir\test.arc" -w "   Compress: %{http_code}`n" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$dataDir\api_text.txt" "$baseUrl/v1/compress"
curl.exe -s -o "$tmpDir\test.out" -w "   Decompress: %{http_code}`n" -X POST -H "Content-Type: application/octet-stream" --data-binary "@$tmpDir\test.arc" "$baseUrl/v1/decompress"

$hash1 = (Get-FileHash "$dataDir\api_text.txt").Hash
$hash2 = (Get-FileHash "$tmpDir\test.out").Hash
if ($hash1 -eq $hash2) {
    Write-Host "   Hash Match: PASS" -ForegroundColor Green
} else {
    Write-Host "   Hash Match: FAIL" -ForegroundColor Red
}
