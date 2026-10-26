# מחקר ובסיס המימוש — 21.10.2026

## נקודת הפתיחה ודיווחי השטח

נבדקו הקוד ותגובות ה־[Gist של asheroto](https://gist.github.com/asheroto/5087d2a38b311b0c92be2a4f23f92d3e), כולל תגובות מוקדמות דרך GitHub API.

- [הצלחה מפורשת עם Installation Assistant, מרץ 2025](https://gist.github.com/asheroto/5087d2a38b311b0c92be2a4f23f92d3e?permalink_comment_id=5520310#gistcomment-5520310).
- [אזהרה על HKCU לאחר מעבר לחשבון מנהל אחר, אוגוסט 2025](https://gist.github.com/asheroto/5087d2a38b311b0c92be2a4f23f92d3e?permalink_comment_id=5722081#gistcomment-5722081).
- [כישלון לאחר מעבר הבדיקה הראשונית, ינואר 2026](https://gist.github.com/asheroto/5087d2a38b311b0c92be2a4f23f92d3e?permalink_comment_id=5955226#gistcomment-5955226): האשף נפתח, אך בדיקה נוספת חסמה את ההתקנה.
- [דיווח המחבר על בדיקה חיה נוספת ב־25H2](https://gist.github.com/asheroto/5087d2a38b311b0c92be2a4f23f92d3e?permalink_comment_id=5956285#gistcomment-5956285), ו־[כישלון דומה במאי 2026](https://gist.github.com/asheroto/5087d2a38b311b0c92be2a4f23f92d3e?permalink_comment_id=6167633#gistcomment-6167633).

דיווחי הצלחה אחרים באותו דיון כוללים מסלולי USB/setup.exe; הם אינם מאמתים את מסלול ה־Assistant המבוקש. דיווחי משתמשים אינם ניסוי מבוקר שלנו. מסקנתי: צריך לבנות ניסיון אמיתי, אך אין בסיס להבטחה כללית.

## ההגדרות המרכזיות

| מנגנון | מה ממומש ומה גבולו |
|---|---|
| `HwReqChkVars` | ארבע רשומות `SQ_*` מהקוד נכתבות כ־MULTI_SZ תוך שימור רשומות אחרות. זהו דיווח מטמון, לא שינוי חומרה. |
| `AllowUpgradesWithUnsupportedTPMOrCPU` | ערך DWORD ב־MoSetup. אין להסיק ממנו לבדו שכל בדיקות ה־Assistant יבוטלו או שהעדר TPM מוחלט יאושר. |
| `UpgradeEligibility` | נכתב לפי ה־SID המקורי ולפי חשבון המנהל המפעיל, כדי לטפל בהבדלי הקשר ההרשאות. |
| מטמוני תאימות | ערכיהם מגובים ומנוקים לפי בחירה. בנייה מחדש על ידי Windows יכולה לבטל את השפעת הניקוי. |

## LabConfig ושאר הבדיקות

[ניתוח ישיר של winsetup.dll בדיון Rufus #1990](https://github.com/pbatard/rufus/issues/1990) מפרט שימוש ב־`BypassRAMCheck`,‏ `BypassSecureBootCheck` ו־`BypassTPMCheck`. באותו ניתוח בדיקת ליבות/תדר אינה משתמשת במתג עקיפה, ו־`BypassStorageCheck` מתואר כחסר השפעה; מנהל Rufus אישר הסרתו. זהו ניתוח של גרסאות Setup מסוימות, לא התחייבות לגבי ה־Assistant העדכני.

לכן מומשו רק שלושת ערכי LabConfig המבוססים, כברירה ניסיונית כבויה. לא נוספו שמות כגון `BypassCPUCheck` או `BypassStorageCheck` רק משום שהם נפוצים ברשת. אין ניחוש של שדות `SQ_*` נוספים שאין להם בסיס שנבדק כאן. אין עקיפה של safeguard holds, של דרייברים או של הרשאות ארגוניות.

## האשף שנבדק מקומית

המקור הוא [דף הורדת Windows 11 של Microsoft](https://www.microsoft.com/en-us/software-download/windows11), קישור האשף `https://go.microsoft.com/fwlink/?linkid=2171764`.

בתאריך הבדיקה הוא הפנה אל:

```text
https://download.microsoft.com/download/e4bc63b3-16f5-450f-a983-c4fc924b97d5/Windows11InstallationAssistant.exe
FileVersion: 1.4.19041.7779
OriginalFilename: WindowsInstallationAssistant.exe
Size: 4184360 bytes
SHA256: A54D847BC7DA71E7E291BA321C80CF6E2F0345B20E399F11670378912D0DB14A
Signer: CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US
```

הקובץ אומת גם ב־Get-AuthenticodeSignature וגם ב־WinVerifyTrust שבאבטיפוס. הוא לא הופעל. חילוץ מחרוזות UTF-16 מקומי מצא `SkipCompatCheck`,‏ `/SkipCompatCheck`,‏ `NoRestartUI`,‏ `/NoRestartUI` ו־`Windows10UpgraderApp.exe`. [דיון Community Hub עם ממצאי Strings](https://techcommunity.microsoft.com/discussions/windows11/windows11installationassistant-exe-command-line-options/3932100) מתעד גם הוא שמות אלה; זהו דיווח קהילתי ולא חוזה API רשמי.

`/SkipCompatCheck` מוצע כאפשרות ניסיונית כבויה. הימצאות מחרוזת אינה מוכיחה שהדגל מכסה כל שלב, ובפרט אינה מוכיחה עקיפת בדיקה חיה לאחר מסירת התהליך ל־Setup. לא הופעל כאן ניסוי שדרוג כדי להוכיח זאת.

## אתחולים ושמירת נתונים

[תיעוד Microsoft של פרמטרי Windows Setup](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/windows-setup-command-line-options?view=windows-11) מתייחס ל־`setup.exe`. אין להניח ש־`/NoReboot`,‏ `/SkipFinalize`,‏ `/Auto Upgrade` או `/Compat IgnoreWarning` מוכרים ל־Installation Assistant או מועברים ממנו. `IgnoreWarning` גם אינו הוכחה להסרת hard block.

`NoRestartUI` אינו מוצג ככלי מובטח למניעת אתחול. האבטיפוס אינו משתמש בו. בהעדר שליטה מאומתת באתחול, יש אישור נפרד וברור **לפני הפעלת האשף**, שמכסה גם את האתחולים שהוא עשוי לבצע בהמשך. אין לחיצה אוטומטית על EULA ואין התקנה שקטה. זו גם הסיבה שלא הופעל האשף על מחשב העבודה לצורך הבדיקות.

הכלי אינו בוחר מסלול Clean/DataOnly, אינו מריץ מתקין אחר ואינו משנה את קובצי ההתקנה. חלון Microsoft נשאר באחריות המשתמש. אין הוכחה ניסויית כאן לשימור נתונים לאחר שדרוג; זאת יש לאמת במכונת ניסוי עם קבצים ויישומים לפני ואחרי.

## חומרה אמיתית

[דרישות Microsoft](https://support.microsoft.com/en-us/windows/experience/compatibility/windows-11-system-requirements) כוללות RAM, נפח אחסון, קושחה ואבטחה. שינוי מספר מדווח אינו יוצר אותם. [מדריך Intel](https://cdrdv2-public.intel.com/868137/325462-089-sdm-vol-1-2abcd-3abcd-4.pdf) מגדיר ביטים נפרדים ל־SSE4.2 ול־POPCNT; הקוד בודק אותם בנפרד ב־CPUID.1.ECX. זוהי רצפת בטיחות שמרנית של האב טיפוס למערכות החדשות, ולא בדיקת תאימות מלאה לגרסת היעד הלא ידועה מראש.

הכלי אינו מאמת רשימות מעבדים רשמיות או מבטיח פתרון לכל מחסור חומרה. הוא גם אינו טוען ש־TPM/Secure Boot הופעלו בגלל כתיבת ערכי המטמון.

## השלב שעדיין חסר לו פתרון מאומת

המעבר מאישור זכאות ראשוני באשף אל בדיקת החומרה החיה של מנוע ההתקנה, לרבות לאחר רענון/העברת תהליך. הראיות לכשל הזה הן דיווחי ינואר ומאי המקושרים לעיל; הן אינן כשל ששוחזר אצלנו. עקיפות Registry ו־SkipCompatCheck נשארות ניסיון מוגבל בשלב זה. נדרש ניסוי על Windows 11 ישן עם חומרה בלתי נתמכת, תיעוד גרסת האשף ותוצאות עדכניות מתוך Panther כדי לקבוע מה עדיין חוסם. לא הוחלף הפרויקט במסלול ISO, USB, enablement package או בכלי אבחון בלבד.
