# Verify only hand-written sources and project text; generated bin/obj and upstream skills are excluded.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName Microsoft.CodeAnalysis.CSharp
Write-Output "Parser=$([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree].Assembly.FullName)"
$sourcePaths = @(& rg --files src tools tests workers runtime-profiles scripts docs packaging -g '*.cs' -g '*.csproj' -g '*.props' -g '*.axaml' -g '*.manifest' -g '*.md' -g '*.json' -g '*.ps1' -g '*.py' -g '*.txt' -g '*.in' -g '*.lock' -g '!**/obj/**' -g '!**/bin/**')
$sourcePaths += @('README.md','CONTRIBUTING.md','LICENSE','NOTICE','global.json','Directory.Build.props','Directory.Packages.props','LiveLingo.slnx','nuget.config')
$failures = [Collections.Generic.List[string]]::new()
$checkedDeclarations = 0
$utf8 = [Text.UTF8Encoding]::new($false,$true)
foreach ($relativePath in $sourcePaths) {
    $path = Join-Path $repoRoot $relativePath
    $bytes = [IO.File]::ReadAllBytes($path)
    try { $text = $utf8.GetString($bytes) } catch { $failures.Add("${relativePath}: invalid UTF-8"); continue }
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) { $failures.Add("${relativePath}: BOM") }
    if ($text.Contains("`r")) { $failures.Add("${relativePath}: CR/CRLF instead of LF") }
    if (-not $text.EndsWith("`n")) { $failures.Add("${relativePath}: missing final LF") }
    if (-not $path.EndsWith('.cs')) { continue }
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text)
    $root = $tree.GetRoot()
    foreach ($node in $root.DescendantNodes()) {
        $kind = $node.GetType().Name
        if ($kind -notin @('ClassDeclarationSyntax','StructDeclarationSyntax','RecordDeclarationSyntax','InterfaceDeclarationSyntax','EnumDeclarationSyntax','DelegateDeclarationSyntax','MethodDeclarationSyntax','ConstructorDeclarationSyntax','LocalFunctionStatementSyntax','OperatorDeclarationSyntax','ConversionOperatorDeclarationSyntax','DestructorDeclarationSyntax')) { continue }
        $checkedDeclarations++
        $line = $tree.GetLineSpan($node.Span).StartLinePosition.Line + 1
        $trivia = ($node.GetLeadingTrivia() | ForEach-Object { $_.ToFullString() }) -join ''
        if ($trivia -notmatch '///') { $failures.Add("${relativePath}:${line}: missing XML documentation ($kind)"); continue }
        $xmlText = ([regex]::Matches($trivia, '(?m)^\s*/// ?(.*)$') | ForEach-Object { $_.Groups[1].Value }) -join "`n"
        try { $xml = [xml]("<root>" + $xmlText + "</root>") } catch { $failures.Add("${relativePath}:${line}: invalid XML documentation"); continue }
        if ($null -ne $xml.root.inheritdoc) { continue }
        if ($null -eq $xml.root.summary) { $failures.Add("${relativePath}:${line}: missing summary") }
        if ($null -ne $node.ParameterList) {
            foreach ($parameter in $node.ParameterList.Parameters) {
                $parameterName = $parameter.Identifier.ValueText
                if ($parameterName -notin @($xml.root.param | ForEach-Object { $_.name })) { $failures.Add("${relativePath}:${line}: missing param $parameterName") }
            }
        }
        if ($null -ne $node.TypeParameterList) {
            foreach ($parameter in $node.TypeParameterList.Parameters) {
                if ($parameter.Identifier.ValueText -notin @($xml.root.typeparam | ForEach-Object { $_.name })) { $failures.Add("${relativePath}:${line}: missing typeparam") }
            }
        }
        if ($kind -in @('MethodDeclarationSyntax','LocalFunctionStatementSyntax','DelegateDeclarationSyntax') -and $node.ReturnType.ToString() -ne 'void' -and $null -eq $xml.root.returns) { $failures.Add("${relativePath}:${line}: missing returns") }
    }
}
Write-Output "Files=$($sourcePaths.Count); Declarations=$checkedDeclarations; Failures=$($failures.Count)"
$failures | Write-Output
if ($failures.Count -gt 0) { exit 1 }
