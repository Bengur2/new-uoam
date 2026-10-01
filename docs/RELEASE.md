# Vydání nové verze a automatické aktualizace

Hráči mají mapu jako zip z GitHub Releases. Při každém spuštění se mapa podívá, jestli je venku
novější verze, a nabídne ji: okno s popisem změn a tlačítky **Aktualizovat / Později**. Ručně jde
totéž přes **Mapa > Zkontrolovat aktualizace…**. Markery a nastavení hráčů se aktualizací
nemění.

## Jak vydat novou verzi

1. Změny commitni a pushni (vydává se přesně to, co je na `origin/main`).
2. Zvyš `<Version>` v `src/NewUOAM.App/NewUOAM.App.csproj` (např. `1.0.0` → `1.1.0`), commitni a
   pushni.
3. Napiš poznámky pro hráče do souboru (zobrazí se v okně aktualizace i na stránce release):

   ```
   - Track mapa: nové okno s hráči nalezenými přes Tracking
   - Oprava: chat se při přepnutí do kompaktního režimu už nezavře
   ```

4. Spusť:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\Release\release.ps1 -NotesFile poznamky.txt            # jen sestaví
   powershell -ExecutionPolicy Bypass -File tools\Release\release.ps1 -NotesFile poznamky.txt -Publish   # sestaví a vydá
   ```

   Bez `-Publish` jen sestaví `publish\app\<verze>\` (zip + `update.json`), takže si to můžeš
   nejdřív vyzkoušet. S `-Publish` navíc ověří, že nemáš necommitnuté změny a HEAD = `origin/main`,
   a založí GitHub release `v<verze>` se zipem a `update.json`.
5. Hotovo. Mapy hráčů ji nabídnou při příštím spuštění.

Stahování jde z GitHubu, relay server (VM) se ho vůbec netýká.

**Špatná verze venku?** Mapa nikdy nenabídne nižší verzi, než má. Oprava = vydat další vyšší verzi
(třeba s revertnutým kódem). Případně release na GitHubu smazat nebo označit jako pre-release,
pak ho mapy přestanou nabízet (berou jen poslední ne-pre-release).

## Podpisový klíč (důležité)

- `update-signing.key` v kořeni projektu je **soukromý klíč**, kterým se podepisuje každá verze.
  Mapa přijme jen aktualizaci podepsanou tímhle klíčem (veřejná část je zabudovaná v
  `src/NewUOAM.Updates/UpdateFeed.cs`, `PublicKey`). Kdo by podvrhl stažení, nebo se dostal k
  GitHub účtu, nemůže hráčům nic podstrčit.
- **Není v gitu** (`*.key` v `.gitignore`). Stačí ho mít na PC, ze kterého vydáváš, ale **zálohuj
  ho mimo to PC** (flash disk, správce hesel). Kdyby PC odešlo a záloha nebyla, platí odstavec
  níže.
- **Když se ztratí**, nová verze nejde vydat tak, aby ji stávající mapy přijaly. Pak:
  `dotnet run --project tools/NewUOAM.ReleaseTool -- keygen --key update-signing.key` vytvoří nový
  pár, vypsaný veřejný klíč patří do `UpdateFeed.PublicKey`, a hráči si **jednou** musí stáhnout
  novou verzi ručně (zip z GitHubu). Od té doby zase jedou automaticky.

## Co se při aktualizaci děje (a proč to nerozbije markery a nastavení)

- Ve složce mapy jsou **jen soubory aplikace**. Nastavení je v `%LocalAppData%\NewUOAM\settings.json`,
  barevné mapy v `%LocalAppData%\NewUOAM\colormap-cache`, markery ve složce, kterou si hráč
  zvolil (nebo `%LocalAppData%\NewUOAM\markers`). Aktualizace na nic z toho nesahá.
- Mapa stáhne zip, ověří podpis manifestu a SHA-256 zipu, rozbalí ho do
  `%LocalAppData%\NewUOAM\updates\` a pak vymění soubory ve své složce: starý soubor přejmenuje na
  `*.old-update`, nový nakopíruje. Windows přejmenování běžící aplikace dovolí. Když cokoli selže,
  vrátí všechno zpátky (nic se nezmění). Pak se mapa zavře (uloží nastavení, odhlásí se z relay) a
  spustí novou verzi. Ta při startu `*.old-update` smaže.
- Mění se jen soubory uvedené v `NewUOAM.files` (seznam souborů balíčku). Cokoli jiného, co si
  hráč do složky dal, zůstane.
- Vývojová verze (`dotnet build`, `bin\Debug`) nemá `NewUOAM.files`, a tak se nikdy sama
  neaktualizuje.

## Pravidla pro nastavení (aby nová verze nerozbila staré `settings.json`)

`AppSettings` se čte přes System.Text.Json. Neznámé pole se ignoruje a chybějící dostane výchozí
hodnotu. Proto:

- **Nový setting** = nová vlastnost s výchozí hodnotou v inicializátoru (`= true`, jinak
  `default`). Stará verze ho nezná a ignoruje, nová ho u starého souboru doplní.
- **Zrušený setting** = vlastnost smazat. Hodnota ve starém souboru se ignoruje a při dalším
  uložení zmizí.
- **Nikdy nepřejmenovávat** vlastnost (hráč by o hodnotu přišel). Když je to nutné, nech starou
  vlastnost a při načtení její hodnotu přenes do nové.
- **Nikdy neměnit typ** vlastnosti (třeba `bool` → `string`). Deserializace by selhala, a
  `AppSettings.Load` pak vrací **celé** nastavení na výchozí hodnoty. Místo toho přidej novou
  vlastnost s novým jménem.
- Totéž platí pro formát souborů markerů (`.map`/`.csv`): ten se nemění, ty soubory čte i starý
  UOAM a Orion.

## Pro hráče: první instalace

Web s popisem a tlačítkem ke stažení: **https://bengur2.github.io/new-uoam/** (zdroj
`docs/index.html` + `docs/img/`. GitHub Pages ho servíruje přímo ze složky `docs/` větve main, takže
každý push ho do minuty aktualizuje).

1. Stáhni instalátor `NewUOAM-Setup.exe`. Odkaz
   https://github.com/Bengur2/new-uoam/releases/latest/download/NewUOAM-Setup.exe vede vždy na
   nejnovější verzi.
2. Spusť ho. Windows možná napoprvé ukáže „Systém Windows ochránil váš počítač“ (instalátor není
   podepsaný certifikátem): **Další informace → Přesto spustit**. Instaluje se jen pro tvůj účet do
   `%LocalAppData%\Programs\NewUOAM`, bez práv správce, se zástupcem ve Startu (a na ploše, když ho
   zaškrtneš). U automatických aktualizací se upozornění už neopakuje.
3. Odinstalace: Nastavení Windows > Aplikace > new UOAM. Nastavení a markery v
   `%LocalAppData%\NewUOAM` zůstanou.

Bez instalátoru: zip `NewUOAM-<verze>-win-x64.zip` z téže stránky rozbal kamkoli, kam se dá
zapisovat (**ne** `Program Files`), a spusť `NewUOAM\NewUOAM.App.exe`.

.NET instalovat není potřeba, balíček ho obsahuje (instalátor ~51 MB, zip ~68 MB).

**Instalátor** = `installer/NewUOAM.iss` (Inno Setup 6, čeština z jeho `Czech.isl`). Sestavuje ho
`release.ps1` ze stejné složky jako zip, včetně `NewUOAM.files`, takže se nainstalovaná kopie
aktualizuje stejně. Na PC, ze kterého se vydává, musí být Inno Setup
(`winget install JRSoftware.InnoSetup --scope user`). Ikonu kreslí `tools/Release/make-icon.ps1`.

## Testování bez GitHubu

`release.ps1 -NotesFile … -PackageBaseUrl http://localhost:27999/` sestaví balíček, který se stahuje
z lokálního serveru, a mapa spuštěná s `--update-feed http://localhost:27999/update.json` ho
nabídne (podpis se kontroluje stejně). Takhle byla aktualizace 1.0.0 → 1.0.1 ověřená 2026-10-01
(viz CLAUDE.md, „Self-update“).
