Add-Type -Path "c:\Rider\DockerTool\src\DockerManager.App\bin\Release\net10.0-windows\win-x64\DockerManager.App.dll"

$cred = [DockerManager.App.Services.CredentialService]::new()
$sett = [DockerManager.App.Services.SettingsService]::new($cred)
$prof = [DockerManager.App.Services.GitHubProfileService]::new($sett, $cred)
$dock = [DockerManager.App.Services.DockerService]::new($sett, $cred)
$auth = [DockerManager.App.Services.GitHubAuthService]::new()

$vm = [DockerManager.App.ViewModels.SettingsViewModel]::new($sett, $cred, $prof, $dock)
Write-Output "ViewModel OK"

if ([System.Windows.Application]::Current -eq $null) {
    $app = [DockerManager.App.App]::new()
    $app.InitializeComponent()
}

try {
    $dlg = [DockerManager.App.Views.SettingsDialog]::new($vm, $auth, $cred, $sett)
    Write-Output "SettingsDialog OK!"
} catch {
    Write-Output "EXCEPTION: "
    Write-Output $_.Exception.ToString()
    if ($_.Exception.InnerException) {
        Write-Output "INNER EXCEPTION: "
        Write-Output $_.Exception.InnerException.ToString()
    }
}
