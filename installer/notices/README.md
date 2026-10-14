# Distribution notices

`Build.ps1` packages required notices in the installation's `licenses` folder.
It reads native and icon notices from their existing repository owners and
package notices from the dependencies listed in the published application's
`.deps.json`. Build-only packages are excluded. SQLite is public domain, and
Boost's licence exempts compiled object code from notice inclusion.

These two packages declare licence expressions without shipping licence files,
so their required distribution texts live here:

- `Microsoft.Data.Sqlite.txt`: MIT, from
  <https://github.com/dotnet/efcore/blob/v10.0.12/LICENSE.txt>.
- `SQLitePCLRaw.txt`: Apache-2.0, from the commit recorded in the 3.0.5 package:
  <https://github.com/ericsink/SQLitePCL.raw/blob/ed046114d5a30534e13294d94d78eb73de896ad4/LICENSE.TXT>.
  The package's copyright notice precedes the licence text.

Credits links to licence sites; the application does not embed these texts.
The [licence review](../../docs/licences.md) records the scope and remaining
distribution questions.
