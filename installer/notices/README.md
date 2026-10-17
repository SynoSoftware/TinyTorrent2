# Distribution notices

`Build.ps1` packages required notices in the installation's `licenses` folder.
It reads native and icon notices from their existing repository owners and
package notices from the dependencies listed in the published application's
`.deps.json`. Build-only packages are excluded. SQLite is public domain, and
Boost's licence exempts compiled object code from notice inclusion.

These packages declare licence expressions without shipping licence files,
so their required distribution texts live here:

- `Microsoft.Data.Sqlite.txt`: MIT, from
  <https://github.com/dotnet/efcore/blob/v10.0.12/LICENSE.txt>.
- `SQLitePCLRaw.txt`: Apache-2.0, from the commit recorded in the 3.0.5 package:
  <https://github.com/ericsink/SQLitePCL.raw/blob/ed046114d5a30534e13294d94d78eb73de896ad4/LICENSE.TXT>.
  The package's copyright notice precedes the licence text.
- `MaxMind.Db.txt`: Apache-2.0, the reader of the DB-IP location files:
  <https://github.com/maxmind/MaxMind-DB-Reader-dotnet/blob/main/LICENSE>.

`DB-IP.txt` is the attribution that DB-IP's CC BY 4.0 data requires. The
flag font's notice is `lib/Lucide/LICENSE.Twemoji`, beside the Lucide notice.

`Win2D.txt` preserves the native map renderer's MIT notice from
<https://github.com/microsoft/Win2D/blob/master/LICENSE.txt>.

Credits links to licence sites; the application does not embed these texts.
The [licence review](../../docs/contracts/licences.md) records the scope and remaining
distribution questions.
