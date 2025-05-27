$scriptPath = $MyInvocation.MyCommand.Path
$scriptDirectory = Split-Path -Parent $scriptPath
Set-Location (Split-Path -Parent $scriptDirectory)
Write-Output "Creating a Python environment..."
if (-Not (Test-Path -Path ./.venv)) {
    &uv venv
}
Write-Output "Installing python packages..."
&uv sync
Write-Output "Creating a Bonsai environment and installing packages..."
Set-Location "bonsai"
.\setup.ps1
Set-Location ..