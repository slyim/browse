param([Parameter(Mandatory=$true)][ValidatePattern('^SearchWindows-[0-9a-f]{32}$')][string]$Name)
$ErrorActionPreference = 'Stop'
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $Name, [System.IO.Pipes.PipeDirection]::In)
try {
    $pipe.Connect(1500)
    $reader = [System.IO.StreamReader]::new($pipe, [System.Text.Encoding]::UTF8)
    try {
        $json = $reader.ReadToEnd()
        if (-not $json) { throw 'The snapshot is empty or unavailable.' }
        $json
    } finally { $reader.Dispose() }
} finally { $pipe.Dispose() }
