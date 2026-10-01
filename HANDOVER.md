# Předávací protokol — new UOAM (stav k 24. 9. 2026)

Tento dokument je pro pokračování práce na jiném PC (a pro Clauda, který tam projekt otevře).
Podrobná technická dokumentace je v `CLAUDE.md` (hlavně tabulka „Project status at a glance“
nahoře), uživatelský návod je v `README.md`. Tady je to podstatné na jednom místě.

## 1. Co projekt je

Náhrada staré UO Auto-Map (UOAM) pro Ultimu Online: mapa, která ukazuje tvoji polohu s minimálním
zpožděním, polohy kamarádů přes internet, markery budov/dungeonů a chat — včetně zobrazení chatu
přímo ve hře. C# / .NET 8, WPF. Hraje se na shardu **Dark Paradise** (Sphere, jen Felucca) klientem
**OrionUO** (`OrionUO64.exe` + Orion Assistant).

## 2. Co je hotové a jak to funguje

| Oblast | Stav | Jak to funguje (zkráceně) |
|---|---|---|
| Čtení mapy (`.mul` i `.uop`, terén + statiky, barvy z `radarcol.mul`) | hotovo | `src/NewUOAM.MapData` čte přímo soubory klienta |
| Předpočítané barevné mapy + cache na disku | hotovo | `%LocalAppData%\NewUOAM\colormap-cache\`, sama se zneplatní při změně souborů klienta |
| Vykreslování (otočení 45° / sever nahoru, zoom kolečkem, plynulé posouvání) | hotovo | vykreslí se jednou do 3× větší cache, posun je jen výřez — jako stará UOAM |
| **Varianta A** — poloha z Orion skriptu přes UDP | hotovo | `tools/OrionScripts/PositionFeed.oajs` → port 27974 (pro chat ve hře se **nepoužívá**) |
| **Varianta B1** — proxy mezi klientem a serverem | **odloženo** | herní spojení je šifrované; kód zůstal, UI je zakomentované |
| **Varianta B2** — poloha čtením paměti `OrionUO64.exe` | hotovo **na tomto PC**, na druhém nefunguje — viz kap. 4 | jen čte (`ReadProcessMemory`), nic nezapisuje |
| Markery (soubory `.map`/`.csv` jako UOAM/Orion, 199 ikon) | hotovo | tlačítko „Načíst markery“, složka se soubory |
| Multiplayer přes relay server | hotovo, nasazeno | UDP, server `89.168.122.175:27980` (Oracle Cloud, Frankfurt) |
| Místnosti (heslem oddělené skupiny) + admin okno | hotovo, nasazeno | admin vytváří/maže místnosti, hráč zná jen heslo místnosti |
| Chat v místnosti | hotovo, nasazeno | klikni do mapy a piš; nic se nikde neukládá |
| Okamžité připojení/odpojení, barvy hráčů, chybové kódy | hotovo, nasazeno | viz kap. 3 |
| **Chat mapy zobrazený ve hře** | hotovo, ověřeno ve hře | rozhraní UOAssist, které nabízí Orion Assistant — viz kap. 5 |

## 3. Multiplayer — co uživatel vidí

- Panel vlevo, sekce **MULTIPLAYER**: adresa serveru, zobrazované jméno, **barva** (mřížka 72
  čitelných barev + vlastní `#RRGGBB`), heslo místnosti, Připojit / Odpojit / Admin…
- Po připojení se ostatní v místnosti ukážou hned (i když stojí na místě), odchod se ohlásí hned.
- Značka ostatních hráčů je **čtvereček** v jejich barvě; mimo výřez mapy šipka u okraje.
- Chat: zprávy jsou v barvě odesílatele, šedě „X se připojil(a) / odpojil(a)“.
- Chybové kódy při připojení: `101` špatná adresa, `102` chybí heslo místnosti, `103` neviditelné
  jméno (jen mezery/tabulátory…), `104` chyba sítě, `105` „kontaktujte admina mapy“ (ve skutečnosti
  špatné heslo místnosti — záměrně to neříká), `106` server neodpovídá.

## 4. Varianta B2 na druhém PC — VYŘEŠENO 24. 9. 2026

Příčina: na druhém PC běží jiný build, `C:\Games\DP\Ultima Online DP\Orion Launcher\OrionUO64.exe`
v1.0.35.1 (pozor, `C:\Orion Launcher` je tam taky, ale je to jiná verze a nepoužívá se). Struktura
postavy je stejná, posunul se jen globální ukazatel (`+0x3793EE8`). B2 teď pozná známé buildy
podle hashe exe a u **neznámého buildu najde adresu sama** (postava musí být přihlášená). Stavový
řádek napíše verzi, případně automaticky nalezenou adresu, a upozorní na prázdný ukazatel.
Podrobnosti v `CLAUDE.md` (sekce B2, „Multi-build support“). Níže je původní postup, který se
hodí, kdyby se v nové verzi změnila i struktura postavy.

Na tomto PC funguje s tímto klientem:

```
C:\Orion Launcher\OrionUO64.exe
verze 1.0.37.0, velikost 5 886 976 B, změněno 11. 8. 2025
SHA-256 7EE94D35FAAEAA4B149EBCBF2F816C57A39479885AC41254D7EDD609D493E35B
Orion Assistant v3.0.38.0
```

Adresa v paměti (ukazatel na postavu) je **přesně pro tento build**:
`OrionUO64.exe + 0x3796EF8` → ukazatel → postava → `+0x28` X, `+0x2C` Y, `+0x41` Z.

Postup na druhém PC:

1. **Porovnej exe.** V PowerShellu: `Get-FileHash "C:\cesta\k\OrionUO64.exe"` a velikost souboru.
   Když se hash liší → jiný build → adresy nesedí. Oprava = nová hledací seance nástrojem
   `NewUOAM.MemoryScanner` (postup v `CLAUDE.md`, sekce „NewUOAM.MemoryScanner“ a „B2“), pak
   přepsat konstanty v `src/NewUOAM.Positioning/Providers/ProcessMemoryPositionProvider.cs`.
   Hlavní podezřelý: B2 v tom případě **nehlásí žádnou chybu**, jen se poloha nemění (čtení
   ukazatele vrátí nulu a smyčka potichu čeká).
2. **Co píše stavový řádek po „Start“ ve Variantě B2:**
   - seznam klientů je prázdný → neběží proces jménem `OrionUO64` (jiný klient, 32bit Orion…).
   - „Nepodařilo se otevřít proces … pro čtení paměti“ nebo „…přečíst moduly procesu“ → rozdílná
     oprávnění: Orion běží jako správce a appka ne (nebo naopak) → spusť obojí stejně.
   - „Čte paměť procesu …“, ale X/Y se nemění → bod 1 (jiný build), nebo postava není přihlášená.
3. Postava musí být **přihlášená ve hře** (na přihlašovací obrazovce ukazatel neexistuje).

Pozn.: chat ve hře (kap. 5) nezávisí na adresách v paměti — na druhém PC by měl fungovat hned, jak
v klientovi běží Orion Assistant, i kdyby B2 ještě nefungovala (bez B2 se použije jediný přihlášený
klient).

## 5. Chat mapy ve hře — jak to dělala stará UOAM a jak to děláme my

- Stará UOAM **nic nevstřikovala do klienta**. Posílala okenní zprávu Windows asistentovi
  (UOAssist, později Razor) a ten text vypsal lokálně jako systémovou zprávu. Na server nic nejde.
- Tvůj **Orion Assistant** tohle rozhraní poskytuje: v procesu `OrionUO64.exe` vytváří okno
  `UOASSIST-TP-MSG-WND` (a „UOAM UO Fake Window“).
- Appka: text → globální atom Windows → `WM_USER+207` do toho okna (bit „systémová zpráva“, barva
  0x44 zelená). Kód: `src/NewUOAM.Positioning/ClientIntegration/UoAssistTextSender.cs`.
- Píše se do klienta sledovaného ve Variantě B2 (multibox → správné okno); bez B2 do jediného
  přihlášeného klienta, pokud je jen jeden. Vypnout: zaškrtávátko „Zobrazovat chat mapy ve hře“.
- **Diakritika:** systémové zprávy Orion Assistant kreslí starým ASCII fontem, který znaky nad 127
  zahodí (ověřeno testy — nejde to obejít kódováním). Proto se text **jen pro hru** převádí bez
  diakritiky („příliš žluťoučký“ → „prilis zlutoucky“, „uvozovky“ a pomlčky na ASCII). V okně chatu
  aplikace diakritika zůstává.

## 6. Jak to spustit na jiném PC

- Potřeba **.NET 8** (pro spuštění stačí *.NET 8 Desktop Runtime*, pro sestavení *.NET SDK 8+*).
- Sestavení: `dotnet build NewUOAM.slnx -c Debug`, spuštění: `dotnet run --project src/NewUOAM.App`
  nebo rovnou `src\NewUOAM.App\bin\Debug\net8.0-windows\NewUOAM.App.exe` (po sestavení; v gitu
  sestavené soubory nejsou).
- **Zdrojáky jsou na GitHubu** (soukromé repo `new-uoam`, od 25. 9.): na novém PC stačí
  `git clone` a build. V repu záměrně **není** SSH klíč, admin heslo, `publish/` ani `_claude_memory/`.
- Po prvním spuštění vyplň: složku klienta UO → „Načíst mapu“, složku markerů, zobrazované jméno,
  barvu, heslo místnosti. **Nastavení se teď už pamatuje** (byla chyba, která ho při každém startu
  mazala — opraveno 23. 9.). Ukládá se do `%LocalAppData%\NewUOAM\settings.json` na každém PC zvlášť.
- Barevné mapy se na novém PC poprvé předpočítají (desítky sekund), pak se berou z cache.

## 7. Relay server (běží, není potřeba nic dělat)

- `89.168.122.175:27980` UDP, Oracle Cloud Always Free, Ubuntu, služba `uoam-relay` (systemd).
- Nasazená verze je aktuální (obsahuje vše z kap. 3). Postup nasazení nové verze: `CLAUDE.md`,
  sekce „Deployed relay instance“ (publish s **lomítky** `publish/relay`, upload pod dočasným
  jménem, `mv`, restart služby).
- **SSH klíč** `ssh-key-2026-09-22.key`: na hlavním PC v `C:\Users\<uživatel>\Downloads\`, na
  druhém PC v kořeni projektu. **Není v gitu** (`.gitignore`) - na nové PC ho přenes zvlášť.
- **Podpisový klíč aktualizací** `update-signing.key` (od 1. 10. 2026, v kořeni projektu, **není
  v gitu**): bez něj nejde vydat verzi, kterou mapy hráčů přijmou. Stačí na PC, ze kterého se
  vydává, ale měj ho zálohovaný mimo něj (např. flash disk, správce hesel). Na jiné PC ho přenes,
  jen když se bude vydávat odtamtud. Postup vydání: `docs/RELEASE.md`.
- **Admin heslo** relay serveru: soubor `pass_for_adminTool_NEMAZAT.txt` v kořeni projektu, **není
  v gitu** (`.gitignore`), případně na VM v `/etc/systemd/system/uoam-relay.service`.

## 7b. Průběžně sledovat a testovat

- **B2 napříč verzemi Orionu.** Po každé aktualizaci OrionUO (nebo na jiném PC) zkontroluj
  stavový řádek hned po Start ve Variantě B2:
  - „OrionUO x.y.z“ → známá verze, v pořádku.
  - „neznámá verze…“ a pak „Adresa postavy nalezena automaticky: …+0x…“ → automatika zabrala.
    Pošli Claudovi tu adresu (a ověř, že poloha na mapě sedí se hrou), doplní ji do tabulky.
  - Pořád „Adresu postavy zatím nejde najít“, přestože je postava přihlášená → nová verze
    změnila strukturu, je potřeba nová seance se skenerem.
  - Poloha je *skoro* správně, ale nejde přesně s tebou → automatika vybrala špatný objekt,
    nahlas to.

## 8. Co zbývá / nápady

- Opačný směr chatu: psát do chatu mapy přímo ze hry (`--text` jako u UOAM) přes rozhraní UOAssist.
- Barvy hráčů ve hře (převod RGB na odstín UO přes `hues.mul`) — zatím všechno zelené.
- B1 (proxy) — odloženo kvůli šifrování, nezačínat bez výslovného zadání.
- B2: facet je natvrdo Felucca; jméno postavy z titulku okna může krátce ukázat jiný titulek.
- Relay: jména jsou od 25. 9. jedinečná v rámci místnosti (chyba 107), po pádu appky je jméno
  blokované až 15 s.
- K ručnímu vyzkoušení: zpráva s „uvozovkami“, pomlčkou a trojtečkou do hry (převod je ověřený jen
  na ukázkových textech).

## 9. Paměť Clauda

Ve složce `_claude_memory/` jsou soubory Claudovy paměti (jen lokálně - **není v gitu**, přenáší
se zvlášť, např. zipem). Na novém PC je zkopíruj do
`%USERPROFILE%\.claude\projects\<klíč projektu>\memory\` — klíč je cesta k projektu s `\` a `:`
nahrazenými `-` (pro `C:\Temp\new_UOAM` to je `c--Temp-new-UOAM`). Není to nutné: `CLAUDE.md` a
tento protokol obsahují všechno podstatné; stačí Claudovi na začátku říct „přečti HANDOVER.md“.
