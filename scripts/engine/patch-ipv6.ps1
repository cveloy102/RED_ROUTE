param([Parameter(Mandatory)][string]$Source)
$ErrorActionPreference = 'Stop'
function Patch([string]$Relative, [string]$Old, [string]$New) {
    $path = Join-Path $Source $Relative
    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $Old = $Old.Replace("`r`n", "`n"); $New = $New.Replace("`r`n", "`n")
    if ($text.Contains($New)) { return }
    if (!$text.Contains($Old)) { throw "Pinned IPv6 patch no longer matches $Relative" }
    [IO.File]::WriteAllText($path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($false))
}
Patch 'option/rule_action.go' "type RouteActionResolve struct {`n" "type RouteActionResolve struct {`n`tOverrideDestination bool ``json:`"override_destination,omitempty`"```n"
Patch 'route/rule/rule_action.go' "return &RuleActionResolve{`n" "return &RuleActionResolve{`n`t`t`tOverrideDestination: action.ResolveOptions.OverrideDestination,`n"
Patch 'route/rule/rule_action.go' "type RuleActionResolve struct {`n" "type RuleActionResolve struct {`n`tOverrideDestination bool`n"
Patch 'route/route.go' "func (r *Router) actionResolve(ctx context.Context, metadata *adapter.InboundContext, action *R.RuleActionResolve) error {`n" "func (r *Router) actionResolve(ctx context.Context, metadata *adapter.InboundContext, action *R.RuleActionResolve) error {`n`tredRouteResolveDestination(metadata, action)`n"
Copy-Item -LiteralPath "$PSScriptRoot/redroute_resolve.go" -Destination "$Source/route/redroute_resolve.go" -Force
Copy-Item -LiteralPath "$PSScriptRoot/redroute_ipv6_test.go" -Destination "$Source/route/redroute_ipv6_test.go" -Force
Copy-Item -LiteralPath "$PSScriptRoot/redroute_prematch_test.go" -Destination "$Source/route/redroute_prematch_test.go" -Force
