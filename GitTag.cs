public record struct GitTag(DateTimeOffset TagDate, string Tag);
public record struct DeploymentPath(string Path, string CaddyFile, string CaddyExe);
