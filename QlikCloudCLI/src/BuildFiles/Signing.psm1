#requires -Version 7.4
Set-StrictMode -Version Latest

function Invoke-ArtifactSigning {
    <#
    .SYNOPSIS
    Signs an artifact through an authenticated HTTPS binary endpoint.
    .DESCRIPTION
    Sends the file as application/octet-stream with Bearer authentication and an
    X-Filename header containing its basename. Replaces the input only after a
    complete, bounded binary response. The caller must validate its signature.
    .PARAMETER HttpClient
    Optional caller-owned client for transport tests. Production calls should
    omit this parameter so redirects are disabled by the configured handler.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string] $SigningUrl,
        [Parameter(Mandatory)][string] $ApiToken,
        [Net.Http.HttpClient] $HttpClient
    )

    $ownedClient = $false
    $request = $null
    $response = $null
    $timeout = $null
    $outputStream = $null
    $temporaryPath = $null
    $failure = 'Artifact signing failed.'
    try {
        $endpoint = $null
        if (-not [uri]::TryCreate($SigningUrl, [UriKind]::Absolute, [ref] $endpoint) -or
            $endpoint.Scheme -cne 'https' -or $endpoint.UserInfo -or $endpoint.Fragment) {
            $failure = 'Artifact signing requires an absolute HTTPS endpoint without user information or a fragment.'
            throw $failure
        }
        if ([string]::IsNullOrWhiteSpace($ApiToken) -or $ApiToken.IndexOfAny([char[]] "`r`n") -ge 0) {
            $failure = 'Artifact signing requires a valid API token.'
            throw $failure
        }
        $inputPath = [IO.Path]::GetFullPath($FilePath)
        $inputFile = [IO.FileInfo]::new($inputPath)
        if (-not $inputFile.Exists -or $inputFile.Length -le 0) {
            $failure = 'Artifact signing requires a nonempty input file.'
            throw $failure
        }
        $maximumLength = $inputFile.Length + 16MB
        if ($null -eq $HttpClient) {
            $handler = [Net.Http.HttpClientHandler]::new()
            $handler.AllowAutoRedirect = $false
            $HttpClient = [Net.Http.HttpClient]::new($handler, $true)
            $HttpClient.Timeout = [timespan]::FromSeconds(180)
            $ownedClient = $true
        }
        $timeout = [Threading.CancellationTokenSource]::new([timespan]::FromSeconds(180))
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $endpoint)
        $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $ApiToken)
        $request.Headers.Add('X-Filename', $inputFile.Name)
        $request.Content = [Net.Http.StreamContent]::new([IO.File]::OpenRead($inputPath))
        $request.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
        $request.Content.Headers.ContentLength = $inputFile.Length
        $response = $HttpClient.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) {
            $failure = 'Artifact signing service did not return a successful binary response.'
            throw $failure
        }
        if ($null -eq $response.Content -or $null -eq $response.Content.Headers.ContentType -or
            $response.Content.Headers.ContentType.MediaType -ine 'application/octet-stream') {
            $failure = 'Artifact signing service returned an unexpected content type.'
            throw $failure
        }
        $declaredLength = $response.Content.Headers.ContentLength
        if ($null -ne $declaredLength -and ($declaredLength -le 0 -or $declaredLength -gt $maximumLength)) {
            $failure = 'Artifact signing service returned an invalid response size.'
            throw $failure
        }

        $temporaryPath = Join-Path $inputFile.DirectoryName ('.signing-' + [guid]::NewGuid().ToString('N') + '.tmp')
        $outputStream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $responseStream = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(81920)
        $receivedLength = 0L
        while (($read = $responseStream.ReadAsync($buffer, 0, $buffer.Length, $timeout.Token).GetAwaiter().GetResult()) -gt 0) {
            $receivedLength += $read
            if ($receivedLength -gt $maximumLength) {
                $failure = 'Artifact signing service returned an invalid response size.'
                throw $failure
            }
            $outputStream.Write($buffer, 0, $read)
        }
        if ($receivedLength -le 0 -or ($null -ne $declaredLength -and $receivedLength -ne $declaredLength)) {
            $failure = 'Artifact signing service returned an incomplete or empty response.'
            throw $failure
        }
        $outputStream.Flush($true)
        $outputStream.Dispose()
        $outputStream = $null
        # Release the input stream before replacing its file on Windows.
        $request.Dispose()
        $request = $null
        # The temporary file is on the same filesystem as the destination.
        [IO.File]::Move($temporaryPath, $inputPath, $true)
        $temporaryPath = $null
    }
    catch {
        # Transport exceptions can contain the endpoint or credential. Neither
        # their message nor the response body belongs in build logs.
        throw $failure
    }
    finally {
        try {
            if ($null -ne $outputStream) { $outputStream.Dispose() }
            if ($null -ne $response) { $response.Dispose() }
            if ($null -ne $request) { $request.Dispose() }
            if ($null -ne $timeout) { $timeout.Dispose() }
            if ($ownedClient) { $HttpClient.Dispose() }
            if ($null -ne $temporaryPath -and [IO.File]::Exists($temporaryPath)) {
                [IO.File]::Delete($temporaryPath)
            }
        }
        catch {
            throw 'Artifact signing cleanup failed.'
        }
    }
}

Export-ModuleMember -Function Invoke-ArtifactSigning
