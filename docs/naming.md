# Naming and structure

These rules apply across the repository. Read them before naming or placing a type, member,
file, folder, namespace or resource key, or before adding an interface or a signature, so project
boundaries do not create competing conventions.

## Naming

Treat the codebase as a language. Names form its vocabulary, so keep that vocabulary small, stable,
precise, and unsurprising.

The goal is not short names. The goal is **clear concepts expressed with the minimum distinctions
the reader needs**. Judge a name primarily where it is read and used, not where it is declared.
Let the model, type system, ownership, and structure carry context; the name carries only the
distinctions that remain.

When principles conflict, prefer them in this order:

1. **Clarity at the use site over brevity.** A short declaration that makes every caller guess is
   not a better name.
2. **Domain meaning over mechanical consistency.** A convention must clarify the model rather than
   flatten real distinctions.
3. **Established platform or domain vocabulary over invented terminology.** A second word for one
   concept makes the reader decide whether two concepts exist.
4. **Structure over repeated context.** Repeating the owner, namespace, type, or layer in every name
   hides the distinction the reader actually needs.
5. **One word before two.** Add a second word only when it carries a distinction the use site still
   needs.

New or changed local and member names prefer one word and normally use at most two. First move
context into the owner, type, namespace, or relationship; if a third word still carries a distinction
the structure cannot, keep it and name that distinction in the report. If four or more words seem
necessary, ask the repository owner before settling the name. This keeps the vocabulary small
without hiding a real distinction. A type follows the same guidance before an established boundary
suffix such as `EventArgs` or `Tests`; the suffix stays only when it names a real boundary.
Narrative test method names are exempt from word-count guidance because they state a scenario and
its outcome rather than name a domain concept.

### Carry only the necessary distinctions

- A name says only what its namespace, enclosing type, member type, relationships, and invariants do
  not already say. A local may rely heavily on nearby context; a widely used type or public contract
  may need more of its distinction in the name.
- A type never repeats its namespace: `Syno.TableView.Column`, not `Syno.TableView.TableColumn`.
  A type named exactly like its namespace's last segment also breaks consumers: inside any `Syno.*`
  namespace the name resolves to the namespace, and C# reports CS0118.
- A short name must not collide with a platform type its consumers import. Check WinUI and the
  Windows SDK before settling one: WinUI already defines `Layout`, `ItemsView` and `Glyphs`.
- Name a concept for what it is or does, not for incidental history, storage, transport, calculation,
  or the screen using it. Keep provenance or process words when removing them would change the
  domain meaning — for example, draft and confirmed file choices are different choices.
- Let concepts compose through code structure: prefer `Table.Selection` and `Column.Width` to names
  that concatenate the whole path. Accumulated qualifiers are a prompt to check whether ownership, a
  relationship, a property, a namespace, or a type should carry them.
- Types carry facts such as unit and kind; names carry meaning. Do not restate a type fact in a name
  unless multiple representations coexist at that use site. A `string` must not read like an object,
  nor an `int` like a collection: `string torrent` claims a torrent but holds only a hash, and
  nothing checks the claim.
- Keys name the concept they identify: `TorrentId`, never a bare `Id`. A command or snapshot can
  carry several identities, such as torrent identity and engine-session identity, and a bare `Id`
  does not say which one it holds.
- Use the grammar of the code: types and state are nouns, actions are verbs, booleans are affirmative
  conditions, and collections are plural. Let the owner supply the object of an action when it is
  already clear: `column.Hide()`, not `column.HideColumn()`.
- A boolean switch uses the platform's frame for its kind, so a WinUI reader recognizes it: `Can`
  for a capability (`Column.CanHide`, `Table.CanReorder`, as `ListView.CanReorderItems`),
  `Is…Enabled` for a gesture (`IsMarqueeEnabled`, as `UIElement.IsTapEnabled`), and `Shows` for
  optional chrome (`ShowsFitButton`, as `ListViewBase.ShowsScrollingPlaceholders`). The frame does
  not count toward the word guidance above. A property's change callback drops the frame and keeps
  the concept: `OnReorderChanged` for `CanReorder`, because the frame adds nothing to a callback
  that only that property calls.
- An event whose payload is one value passes it as `EventHandler<T>`: `EventHandler<Selection>
  SelectionChanged`. Otherwise its arguments type is the event's name plus `EventArgs`, with no
  component prefix: `ReorderRequestedEventArgs`, not `TableReorderRequestedEventArgs`. The type's
  namespace already says which component raises it. When that name exists in a platform namespace
  consumers import, such as `SelectionChangedEventArgs`, the collision rule above decides.
- Challenge filler such as `Data`, `Info`, `Detail`, `Model`, `Entity`, `Object`, `Item`, `Manager`,
  `Helper`, `Service`, `Result`, `Request`, and `Response`. These words are not banned; each stays only
  when it names a real domain, UI, boundary, or framework distinction.
- XAML resource keys share one flat dictionary with the platform and every other library, and no
  namespace reaches a `{StaticResource}` lookup. A key therefore carries its component's name, the
  way the platform's `ListViewItemBackground` does: `TableViewRowTemplate`. The required component
  name does not count toward the word guidance above.

### Keep one durable vocabulary

- Domain terms are defined in [CONTEXT.md](../CONTEXT.md) and, for controls, in each library's
  `CONTEXT.md`. Each means one thing in code, tests, and docs; a synonym reads as a second concept.
- Prefer established C#/.NET and WinUI vocabulary when it already names the same concept. A locally
  cleaner synonym makes framework-facing code harder to recognize without improving the model.
- Prefer enduring concepts over temporary architecture. A domain name should normally survive a
  change of transport, UI, framework, storage, or processing implementation.
- A naming convention applies to every entity in the repository or is not adopted. Exceptions turn
  every use site into a fresh decision and make the vocabulary unstable.

### Naming review

For every new or changed name, answer all of these before accepting it:

1. What single concept does it represent, and what is that concept's canonical term?
2. What context is reliably present at its busiest use sites?
3. Which distinctions must the name still carry after that context is considered?
4. Does every word carry one of those distinctions, or repeat the owner, type, layer, or mechanism?
5. Would removing a word lose meaning, or only remove redundancy?
6. If qualifiers accumulate, should the structure express some of them instead?

Do not optimize a name to explain itself in isolation, and do not optimize it for shortness.
**Design the structure so the name only has to say what the structure cannot.**

Renaming a public member changes a contract; review its callers and specification together.
Search literal consumers of resource keys, template parts, and visual states as well as typed
references.

## Files

Start with the existing owner. Add a file when it holds a coherent responsibility that is hard
to read in the owner, or when a framework requires its path. Group small related declarations;
keep framework boundaries thin. The repository owner's 400-byte minimum for hand-authored files
is a fragmentation check: group a smaller file with its owner unless its standalone path is
required by the framework or build. Generated files are exempt. Never pad a file to meet the
minimum. A low caller count is not proof that a cohesive asset is obsolete.

The required central enum vocabulary uses `Enums.cs` in managed projects and
`Enums.h` in the native project. That prescribed home may be smaller than the
fragmentation minimum; keep it concise rather than padding it or scattering enums.

Split code by authority, lifetime, thread ownership or transaction boundary, not by screen, action
or file length: those splits scatter one decision across files that must change together. Code
lives with its owner. Add no dumping-ground file such as `Utils.cs`, `Common.cs` or
`TableHelper.cs`, and no folder by kind such as `Helpers/`, `Models/` or `Converters/`; each
separates code from the decision it serves. The fixed `Themes/` and `Resources/` folders below and
the package folders `Assets/` and `Properties/` are the only folders by kind, apart from the
native engine's `engine/inc/`, which holds its headers by the repository owner's ruling. Extension methods
stay technical and hold no product rule.

A filename follows the type it holds. A type split across files keeps `Table.cs`, and a folder
named after the type holds the other parts, each named by the aspect it holds: `Table/Sorting.cs`,
not `TableSorting.cs`, so no part file repeats its type. Use at most one dot in filenames,
separating the name from its extension. The folder carries context; keep company and namespace prefixes out of filenames.
Preserve exact filenames required by tooling, such as `Directory.Build.props`, `Generic.xaml` and
Windows resource qualifiers.

Do not add a service, helper, interface, adapter, alias, or compatibility shim that only forwards,
renames, or reshapes an existing path, and no mediator, generic repository or facade between a
caller and its owner. Keep compatibility only at a proven external boundary. This preserves one
owner and one route through the behavior.

## Interfaces

Use concrete dependencies by default. Add an interface or abstract base class only when the caller
crosses an external boundary or selects between real implementations, and neither can be expressed
through the existing concrete owner. A hypothetical replacement or mocking ordinary application code
does not establish that need. This keeps one route through behavior instead of a parallel
declaration for every class.

## Inputs and results

A repository-owned method, constructor, factory, delegate or lambda normally takes at most four
caller-supplied parameters. Past four, first correct the responsibility or pass a cohesive type that
already owns the values, not an arbitrary `Options` bag or a tuple: `Mean(Shot shot, Rect area)`,
not `Mean(Shot shot, double x0, double y0, double w, double h)`. If five remain necessary, report
why no cohesive type owns them; if six or more remain, ask the repository owner before settling the
signature. Framework-mandated signatures keep their required shape. This keeps argument lists
readable and stops callers from swapping same-typed arguments without the compiler noticing.

Return multiple values from a non-private member through a named type that states their meaning;
tuples, `std::pair` and clusters of `out` or reference parameters make callers reconstruct it. A
private helper may return a tuple whose elements are named, because its only callers sit beside it.

## Folders and namespaces

Start flat within an owner. Add a subfolder only when it groups substantial supporting files
whose relationship the parent does not already express. Collapse a folder that only wraps one
child folder: an extra level without an ownership distinction makes readers navigate for nothing.
Two folders are fixed in every project that needs them, whatever they hold: `Themes/` for
WinUI's `Generic.xaml`, and `Resources/` for the project's language catalogues (`en.json`).

Namespaces have at most four segments, including the project's root namespace. A subfolder is a
namespace of the same name, so the types in it do not repeat that name: `Header/Strip.cs` holds
`Syno.TableView.Header.Strip`, not `HeaderStrip`. Three folders create no namespace, because what
they hold belongs to the owner's namespace: the fixed `Themes/` and `Resources/`, and a split type's
parts folder such as `Table/`. A part file therefore declares its owner's namespace, not the one
Visual Studio proposes from the folder.

A feature folder holds the types that serve only that feature. A type that serves several, such as
the cells panel the header and the rows both use, lives at the root, so no feature folder depends
on another. A library's API lives in its root namespace, so a consumer needs one `using` and one
XAML prefix. A host of a library, its sample included, has its own root namespace named after its
project (`Syno.TableViewSample`), never one inside the library's: inside it the library's types
resolve without a `using`, so the host would hide what every real consumer has to write.
A library's tests are not a host and keep the library's namespace (`Syno.TableView.Tests`). A type that XAML forces public only to fill a template part stays in its feature
namespace, because no consumer writes it.

Never name a folder after a member of the types that use it. Inside such a type the member hides
the namespace and `Rows.View` fails with CS0119, which is why the table's row folder is `Body`.
A library with a single type puts it in the company namespace under the library's name: the class
`Syno.Lucide`, used as `Lucide.Font`. A `Syno.Lucide` namespace beside it would make the name
resolve to the namespace (CS0118).

Each component is one folder with the same shape: the project in `src/` and, when the component
has them, its tests in `tests/` and a demonstration host in `sample/`. A project folder never
contains another project, because an SDK project compiles every source file under its folder.
Project names are unique in the repository, because each project writes to
`artifacts/bin/<project name>/`.
