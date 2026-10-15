# This script is intentionally identical for both modules. The test harness
# supplies only a connection made by the selected connector.
param([Parameter(Mandatory)]$Connection)
$app = Open-QSEApp -AppId 'portable-app' -QlikClient $Connection
try {
    $layout = $app | Get-QSEAppLayout
    $script = $app | Get-QSEScript
    $directScript = $app.GetScript()
    $syntax = @($app | Test-QSEScript)
    $fields = @($app | Get-QSEField)
    $namedField = Get-QSEField -App $app -Name 'Sales' -State '$'
    $tables = @($app | Get-QSETable)
    $expressionCheck = Test-QSEExpression -App $app -Expression 'Sum(Sales)'
    $expression = Invoke-QSEExpression -App $app -Expression 'Sum(Sales)'
    $lists = [ordered]@{}
    foreach ($kind in @('Sheet', 'Bookmark', 'Dimension', 'Measure', 'MasterObject', 'Field', 'Variable')) {
        $command = 'Get-QSE' + $kind + 'List'
        try { $lists[$kind] = @($app | & $command) }
        catch { throw "$command failed: $($_.Exception.Message)" }
    }
    [pscustomobject]@{
        Layout = $layout; Script = $script; DirectScript = $directScript
        Syntax = $syntax; Fields = $fields; NamedField = $namedField; Tables = $tables
        ExpressionCheck = $expressionCheck; Expression = $expression; Lists = $lists
    }
}
finally { $app | Close-QSEApp }
