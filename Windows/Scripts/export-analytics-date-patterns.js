// Read-only reference export. Run on the Mac reference host with:
// osascript -l JavaScript Windows/Scripts/export-analytics-date-patterns.js > output.json
// The exporter changes no preferences, app files or local account state.
ObjC.import('Foundation');
const patterns = {};
const locales = Array.from(new Set(ObjC.deepUnwrap($.NSLocale.availableLocaleIdentifiers).concat(['en-US','en-GB','de-DE','fr-FR','ko-KR','ja-JP','zh-CN','zh-TW','zh-HK','zh-MO','zh-SG','pl-PL']))).sort();
for (const locale of locales) {
 const f = $.NSDateFormatter.alloc.init;
 f.locale = $.NSLocale.alloc.initWithLocaleIdentifier(locale);
 f.timeZone = $.NSTimeZone.timeZoneForSecondsFromGMT(0);
 f.dateStyle = 2; f.timeStyle = 0;
 const day = ObjC.unwrap(f.dateFormat);
 f.dateStyle = 0; f.timeStyle = 1;
 const time = ObjC.unwrap(f.dateFormat);
 f.dateStyle = 2;
 const key = locale.replace(/_/g, '-');
 const value = [day,time,ObjC.unwrap(f.dateFormat)];
 for (const template of ['jms','j','MMMd'])
   value.push(ObjC.unwrap($.NSDateFormatter.dateFormatFromTemplateOptionsLocale(template, 0, f.locale)));
 if (patterns[key] && JSON.stringify(patterns[key]) !== JSON.stringify(value)) throw new Error('Conflicting locale alias: ' + key);
 patterns[key] = value;
}
const rows = Object.keys(patterns).sort().map(key => '    ' + JSON.stringify(key) + ': ' + JSON.stringify(patterns[key]));
'{\n  "source": "macOS Foundation NSDateFormatter medium date / short time and chart jms, j, MMMd templates",\n  "sourceOS": '
 + JSON.stringify(ObjC.unwrap($.NSProcessInfo.processInfo.operatingSystemVersionString))
 + ',\n  "patterns": {\n' + rows.join(',\n') + '\n  }\n}';
