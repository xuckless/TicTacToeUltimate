// WP1 stub: commands gen, gen --check, solve.
var command = args.Length > 0 ? args[0] : "";
await Console.Error.WriteLineAsync($"RulesTool '{command}': not implemented yet (WP1). Usage: gen [--check] | solve <rulesId>");
return 2;
