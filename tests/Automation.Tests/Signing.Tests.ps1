#requires -Version 7.4
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../../QlikCloudCLI/src/BuildFiles/Signing.psm1') -Force
if (-not ('SigningTestHandler' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public sealed class SigningTestHandler : HttpMessageHandler
{
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string ContentType = "application/octet-stream";
    public byte[] ResponseBytes = new byte[] { 5, 0, 255, 6 };
    public long? DeclaredLength;
    public bool UnknownLength;
    public string Failure;
    public string Method;
    public string Filename;
    public string Authorization;
    public string RequestContentType;
    public byte[] RequestBytes;
    public int Calls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Method = request.Method.Method;
        Filename = string.Join("", request.Headers.GetValues("X-Filename"));
        Authorization = request.Headers.Authorization.ToString();
        RequestContentType = request.Content.Headers.ContentType.MediaType;
        RequestBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (Failure != null) throw new HttpRequestException(Failure);
        var response = new HttpResponseMessage(Status);
        response.Content = UnknownLength ? (HttpContent)new SigningTestContent(ResponseBytes) : new ByteArrayContent(ResponseBytes);
        if (ContentType != null) response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType);
        if (DeclaredLength != null) response.Content.Headers.ContentLength = DeclaredLength;
        if ((int)Status >= 300 && (int)Status < 400) response.Headers.Location = new Uri("https://redirect.invalid/private");
        return response;
    }
}
public sealed class SigningTestContent : HttpContent
{
    private readonly byte[] bytes;
    public SigningTestContent(byte[] value) { bytes = value; }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) { return stream.WriteAsync(bytes, 0, bytes.Length); }
    protected override Task<Stream> CreateContentReadStreamAsync() { return Task.FromResult<Stream>(new MemoryStream(bytes, false)); }
}
'@
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('qlikcloud-signing-tests-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($temporary)
$passed = 0
$inputPath = Join-Path $temporary 'Artifact.dll'
$unsigned = [byte[]] (1, 0, 128, 255, 2)
$endpoint = 'https://signer.invalid/private-endpoint'
$token = 'test-token-that-must-not-appear-in-errors'
function Assert-Test([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message }; $script:passed++ }
function Invoke-TestCase([SigningTestHandler] $Handler, [bool] $ExpectFailure, [string] $Url = $endpoint) {
    [IO.File]::WriteAllBytes($inputPath, $unsigned)
    $client = [Net.Http.HttpClient]::new($Handler, $true)
    $failure = $null
    try {
        Invoke-ArtifactSigning -FilePath $inputPath -SigningUrl $Url -ApiToken $token -HttpClient $client
    }
    catch { $failure = $_ }
    finally { $client.Dispose() }
    Assert-Test (($null -ne $failure) -eq $ExpectFailure) 'The signing transport returned an unexpected outcome.'
    if ($ExpectFailure) {
        $errorText = ($failure | Out-String) + $failure.Exception.ToString()
        Assert-Test (-not $errorText.Contains($endpoint) -and -not $errorText.Contains($token) -and -not $errorText.Contains('private response detail')) 'Errors must not disclose credentials, endpoint or response body.'
        Assert-Test ($null -eq $failure.Exception.InnerException) 'Signing errors must not retain a sensitive inner exception.'
        Assert-Test ([Convert]::ToHexString([IO.File]::ReadAllBytes($inputPath)) -ceq [Convert]::ToHexString($unsigned)) 'Failed signing must preserve the original file.'
    }
    Assert-Test (@(Get-ChildItem -LiteralPath $temporary -Filter '.signing-*.tmp').Count -eq 0) 'Temporary signing responses must be removed.'
}
try {
    $success = [SigningTestHandler]::new()
    Invoke-TestCase $success $false
    Assert-Test ($success.Method -ceq 'POST') 'Signing must POST raw bytes.'
    Assert-Test ($success.Filename -ceq 'Artifact.dll') 'Only the basename belongs in the filename header.'
    Assert-Test ($success.Authorization -ceq "Bearer $token") 'Signing requires Bearer authentication.'
    Assert-Test ($success.RequestContentType -ceq 'application/octet-stream') 'Signing requires binary request content.'
    Assert-Test ([Convert]::ToHexString($success.RequestBytes) -ceq [Convert]::ToHexString($unsigned)) 'The request must preserve binary content.'
    Assert-Test ([Convert]::ToHexString([IO.File]::ReadAllBytes($inputPath)) -ceq [Convert]::ToHexString($success.ResponseBytes)) 'Successful signing must replace the file with the complete response bytes.'

    $failed = [SigningTestHandler]::new()
    $failed.Status = [Net.HttpStatusCode]::Unauthorized
    $failed.ResponseBytes = [Text.Encoding]::UTF8.GetBytes("private response detail $endpoint $token")
    Invoke-TestCase $failed $true

    $redirect = [SigningTestHandler]::new()
    $redirect.Status = [Net.HttpStatusCode]::TemporaryRedirect
    Invoke-TestCase $redirect $true
    Assert-Test ($redirect.Calls -eq 1) 'Redirect responses must fail without a second request.'

    $wrongContent = [SigningTestHandler]::new()
    $wrongContent.ContentType = 'application/json'
    Invoke-TestCase $wrongContent $true

    $missingContentType = [SigningTestHandler]::new()
    $missingContentType.ContentType = $null
    Invoke-TestCase $missingContentType $true

    $empty = [SigningTestHandler]::new()
    $empty.ResponseBytes = [byte[]]::new(0)
    Invoke-TestCase $empty $true

    $oversized = [SigningTestHandler]::new()
    $oversized.DeclaredLength = 16MB + $unsigned.Length + 1
    Invoke-TestCase $oversized $true

    $unbounded = [SigningTestHandler]::new()
    $unbounded.UnknownLength = $true
    $unbounded.ResponseBytes = [byte[]]::new(16MB + $unsigned.Length + 1)
    Invoke-TestCase $unbounded $true

    $incomplete = [SigningTestHandler]::new()
    $incomplete.DeclaredLength = $incomplete.ResponseBytes.Length + 1
    Invoke-TestCase $incomplete $true

    $transportFailure = [SigningTestHandler]::new()
    $transportFailure.Failure = "Transport failed for $endpoint with $token; private response detail"
    Invoke-TestCase $transportFailure $true

    $invalidEndpoint = [SigningTestHandler]::new()
    Invoke-TestCase $invalidEndpoint $true 'http://signer.invalid/private-endpoint'
    Assert-Test ($invalidEndpoint.Calls -eq 0) 'Non-HTTPS endpoints must be rejected before sending credentials.'
    Invoke-TestCase $invalidEndpoint $true 'https://user:password@signer.invalid/private-endpoint'
    Assert-Test ($invalidEndpoint.Calls -eq 0) 'Embedded endpoint credentials must be rejected.'
    Write-Host "Signing checks passed: $passed"
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'qlikcloud-signing-tests-*') { throw 'Unsafe signing test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
