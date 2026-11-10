param([string]$ManifestPath,[switch]$VerifyDownloadOnly,[string]$OutputDirectory,[switch]$FunctionsOnly)
$ErrorActionPreference='Stop'

function Read-RuntimeManifest([string]$Path) {
    $manifest=Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.Version -notmatch '^10\.0\.\d+$' -or $manifest.Sha512 -notmatch '^[0-9a-fA-F]{128}$'){throw '运行时版本或校验值无效。'}
    $uri=[uri]$manifest.Url
    $expected='/dotnet/WindowsDesktop/'+$manifest.Version+'/windowsdesktop-runtime-'+$manifest.Version+'-win-x64.exe'
    if($uri.Scheme -ne 'https' -or $uri.Host -ne 'builds.dotnet.microsoft.com' -or -not $uri.IsDefaultPort -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -cne $expected){throw '运行时地址不是指定的微软官方 x64 桌面运行时。'}
    return $manifest
}
function Assert-RuntimePackage([string]$Path,$Manifest) {
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash -ne $Manifest.Sha512){throw '运行时下载校验失败，请重新下载。'}
    $signature=Get-AuthenticodeSignature -LiteralPath $Path
    if($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)'){throw '无法验证运行时的微软数字签名。'}
}
function Test-RuntimeExit([int]$Code) {return $Code -eq 0 -or $Code -eq 3010}
if($FunctionsOnly){return}

$exitCode=24;$form=$null;$client=$null;$response=$null;$inputStream=$null;$outputStream=$null;$partial=$null;$package=$null;$ownsPartial=$false;$ownsPackage=$false
$script:runtimeCancel=New-Object Threading.CancellationTokenSource
$script:runtimeMayClose=$false
$script:runtimeUserCancelled=$false
try {
    $manifest=Read-RuntimeManifest $ManifestPath
    Add-Type -AssemblyName System.Net.Http
    if(-not $VerifyDownloadOnly) {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        [Windows.Forms.Application]::EnableVisualStyles()
        $form=New-Object Windows.Forms.Form
        $form.Text='安装必要组件';$form.Width=440;$form.Height=210;$form.StartPosition='CenterScreen';$form.FormBorderStyle='FixedDialog';$form.MaximizeBox=$false;$form.MinimizeBox=$false
        $label=New-Object Windows.Forms.Label;$label.SetBounds(20,20,390,50);$label.Text='正在连接微软官方下载服务…'
        $bar=New-Object Windows.Forms.ProgressBar;$bar.SetBounds(20,80,385,18)
        $cancelButton=New-Object Windows.Forms.Button;$cancelButton.SetBounds(320,115,85,30);$cancelButton.Text='取消'
        $cancelButton.Add_Click({$script:runtimeUserCancelled=$true;$script:runtimeCancel.Cancel()})
        $form.Add_FormClosing({param($sender,$eventArgs)if(-not $script:runtimeMayClose){$eventArgs.Cancel=$true;$script:runtimeUserCancelled=$true;$script:runtimeCancel.Cancel()}})
        $form.Controls.AddRange(@($label,$bar,$cancelButton));$form.Show()
    }
    function Wait-RuntimeTask($Task) {
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while(-not $Task.IsCompleted) {
            if($form){[Windows.Forms.Application]::DoEvents()}
            $script:runtimeCancel.Token.ThrowIfCancellationRequested()
            if($clock.Elapsed.TotalSeconds -gt 90){$script:runtimeCancel.Cancel();throw '连接微软下载服务超时，请检查网络后重试。'}
            Start-Sleep -Milliseconds 25
        }
        return $Task.GetAwaiter().GetResult()
    }
    if(-not $OutputDirectory){$OutputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('Qingxia-Runtime-'+[guid]::NewGuid().ToString('N'))}
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $package=Join-Path $OutputDirectory ('windowsdesktop-runtime-'+$manifest.Version+'-win-x64.exe')
    $partial=$package+'.partial'
    if(Test-Path -LiteralPath $package){throw '目标目录已有安装包，请使用新的下载目录。'}
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    $handler=New-Object Net.Http.HttpClientHandler
    $handler.Proxy=[Net.WebRequest]::GetSystemWebProxy();$handler.Proxy.Credentials=[Net.CredentialCache]::DefaultNetworkCredentials
    $client=New-Object Net.Http.HttpClient($handler);$client.Timeout=[TimeSpan]::FromSeconds(90)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('Qingxia-RuntimeInstaller/0.12.6')
    $script:runtimeCancel.CancelAfter([TimeSpan]::FromMinutes(15))
    $response=Wait-RuntimeTask ($client.GetAsync([uri]$manifest.Url,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$script:runtimeCancel.Token))
    $null=$response.EnsureSuccessStatusCode()
    $length=$response.Content.Headers.ContentLength
    if($length -and ($length -le 0 -or $length -gt 256MB)){throw '运行时文件大小异常。'}
    $inputStream=Wait-RuntimeTask ($response.Content.ReadAsStreamAsync())
    $outputStream=[IO.File]::Open($partial,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $ownsPartial=$true
    $buffer=New-Object byte[] 65536;$total=0L
    while(($count=Wait-RuntimeTask ($inputStream.ReadAsync($buffer,0,$buffer.Length,$script:runtimeCancel.Token))) -gt 0) {
        $script:runtimeCancel.Token.ThrowIfCancellationRequested();$total+=$count
        if($total -gt 256MB){throw '运行时下载超过预期大小。'}
        $outputStream.Write($buffer,0,$count)
        if($form){$label.Text=('正在下载 .NET 10 桌面运行时（x64）'+"`r`n"+('{0:F1} MiB' -f ($total/1MB)));if($length){$bar.Value=[Math]::Min(100,[int]($total*100/$length))};[Windows.Forms.Application]::DoEvents()}
    }
    if($length -and $total -ne $length){throw '运行时下载不完整。'}
    $outputStream.Dispose();$outputStream=$null
    if($form){$label.Text='正在核对安装包和微软数字签名…';[Windows.Forms.Application]::DoEvents()}
    Assert-RuntimePackage $partial $manifest
    $script:runtimeCancel.Token.ThrowIfCancellationRequested();[IO.File]::Move($partial,$package);$ownsPartial=$false;$ownsPackage=$true
    if($VerifyDownloadOnly){[pscustomobject]@{Version=$manifest.Version;Bytes=$total;Sha512Verified=$true;MicrosoftSignatureVerified=$true;Package=$package}|ConvertTo-Json;$exitCode=0}
    else {
        $label.Text='正在安装必要组件，请确认 Windows 管理员授权。';$cancelButton.Enabled=$false;$form.ControlBox=$false;$bar.Style='Marquee';[Windows.Forms.Application]::DoEvents()
        # Only Microsoft's verified installer is elevated. The app's installer stays under the original user.
        $process=Start-Process -FilePath $package -Verb RunAs -ArgumentList @('/install','/quiet','/norestart') -PassThru
        while(-not $process.HasExited){[Windows.Forms.Application]::DoEvents();Start-Sleep -Milliseconds 100}
        if(-not(Test-RuntimeExit $process.ExitCode)){throw ('微软运行时安装未完成，退出码：'+$process.ExitCode)}
        $exitCode=$process.ExitCode
    }
}
catch {
    $failure=$_.Exception
    $cause=$failure;while($cause.InnerException){$cause=$cause.InnerException}
    if($script:runtimeUserCancelled -or ($cause -is [ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 1223)){$exitCode=1602}
    else {Write-Error -Message $failure.Message -ErrorAction Continue;if($form){[Windows.Forms.MessageBox]::Show('必要组件安装未完成。现有程序和数据保留。'+"`r`n"+$failure.Message,'轻匣',[Windows.Forms.MessageBoxButtons]::OK,[Windows.Forms.MessageBoxIcon]::Information)|Out-Null};$exitCode=24}
}
finally {
    foreach($resource in @($outputStream,$inputStream,$response,$client)){if($resource){$resource.Dispose()}}
    if($ownsPartial -and $partial -and (Test-Path -LiteralPath $partial)){Remove-Item -LiteralPath $partial -Force}
    if(-not $VerifyDownloadOnly -and $ownsPackage -and $package -and (Test-Path -LiteralPath $package)){Remove-Item -LiteralPath $package -Force}
    if($form){$script:runtimeMayClose=$true;$form.Close();$form.Dispose()}
    $script:runtimeCancel.Dispose()
}
exit $exitCode
