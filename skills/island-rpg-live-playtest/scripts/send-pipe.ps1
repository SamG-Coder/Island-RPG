param(
    [Parameter(Mandatory = $true)]
    [string]$PipeName,

    [string]$Json,

    [string]$JsonBase64,

    [string]$Command,

    [ValidateRange(100, 60000)]
    [int]$TimeoutMilliseconds = 8000
)

$provided = @($Json, $JsonBase64, $Command).Where({ -not [string]::IsNullOrWhiteSpace($_) })
if ($provided.Count -ne 1) {
    throw 'Specify exactly one of -Json, -JsonBase64, or -Command.'
}

$requestJson = if (-not [string]::IsNullOrWhiteSpace($Command)) {
    @{ command = $Command } | ConvertTo-Json -Compress
}
elseif (-not [string]::IsNullOrWhiteSpace($JsonBase64)) {
    [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($JsonBase64))
}
else {
    $Json
}

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    '.',
    $PipeName,
    [System.IO.Pipes.PipeDirection]::InOut)

try {
    $pipe.Connect($TimeoutMilliseconds)
    $writer = [System.IO.StreamWriter]::new($pipe)
    $writer.AutoFlush = $true
    $reader = [System.IO.StreamReader]::new($pipe)
    $writer.WriteLine($requestJson)
    $response = $reader.ReadLine()
    if ([string]::IsNullOrWhiteSpace($response)) {
        throw 'The game returned an empty control-pipe response.'
    }
    $response
}
finally {
    $pipe.Dispose()
}
