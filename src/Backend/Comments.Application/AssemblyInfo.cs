using System.Runtime.CompilerServices;

// The CAPTCHA implementation lives in Infrastructure and must be able to populate the
// never-serialised challenge code (v1 raised this via same-assembly access).
[assembly: InternalsVisibleTo("Comments.Infrastructure")]
