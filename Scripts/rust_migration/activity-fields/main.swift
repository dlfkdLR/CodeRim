import Foundation

var seed: UInt64 = 0x351559
func random(_ n: Int) -> Int {
  seed = seed &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
  return Int((seed >> 32) % UInt64(n))
}
func edgeCases() {
  let key = Data("key=".utf8)
  var inputs = [
    Data(), Data("prefix;key=one\" key=two\"".utf8), Data("key=bad".utf8),
    Data([107, 101, 121, 61, 0xff, 34]), Data("key=ca\\fé\"".utf8),
  ]
  for n in [0, 1, 15, 16, 127, 2047, 2048, 2049, 2050, 4096] {
    for tail in [Data("\"".utf8), Data("\\a\"".utf8), Data("\\".utf8), Data()] {
      inputs.append(key + Data(repeating: 65, count: n) + tail)
      inputs.append(key + Data(String(repeating: "\\a", count: n).utf8) + tail)
    }
  }
  for _ in 0..<864 {
    let size = random(400)
    var input = Data((0..<size).map { _ in UInt8(random(256)) })
    input.append(key)
    input.append(contentsOf: (0..<random(100)).map { _ in UInt8(random(256)) })
    input.append(contentsOf: [34, 32, 107, 101, 121, 61, 34])
    inputs.append(input)
  }
  for (i, raw) in inputs.enumerated() {
    let pad = i % 23
    let line = (Data(repeating: 46, count: pad) + raw).dropFirst(pad)
    for marker in [key, Data([34]), Data(repeating: 65, count: 129), Data()] {
      for offset in [0, min(1, line.count), line.count / 2, line.count] {
        let result = LogBytes.string(after: marker, in: line, from: line.startIndex + offset)
        print(
          i, offset, LogBytes.contains(line, marker),
          result.map { Data($0.value.utf8).base64EncodedString() + "@" + String($0.end) } ?? "-")
      }
    }
    print(
      "values",
      LogBytes.strings(after: key, in: line).map { Data($0.utf8).base64EncodedString() }.joined(
        separator: ","))
  }
}
func dates() {
  for zone in ["UTC", "Asia/Seoul", "Pacific/Apia", "Australia/Lord_Howe", "America/New_York"] {
    var calendar = Calendar(identifier: .gregorian)
    calendar.timeZone = TimeZone(identifier: zone)!
    let days = ActivityDayResolver(calendar: calendar)
    for month in [0, 1, 2, 3, 4, 11, 12, 13] {
      for day in [0, 1, 2, 10, 28, 29, 30, 31, 32] {
        for year in [1900, 1970, 2000, 2001, 2025, 2026, 2099, 2100] {
          let prefix = String(
            format: "%04d-%02d-%02dT%02d:%02d", year, month, day, (day * 7) % 25, (month * 5) % 62)
          for tail in ["", ":00Z", ".0-02:00"] {
            print(
              "day", zone, prefix,
              days.day(of: prefix + tail)?.timeIntervalSinceReferenceDate.bitPattern.description
                ?? "-")
          }
        }
      }
    }
    for stamp in ["", "오늘", "２０２６-１１-２６T12:30Z", "2000-02-29T22:30", "2026-12-31T23:59+12:00"] {
      print(
        "edgeDay", days.day(of: stamp)?.timeIntervalSinceReferenceDate.bitPattern.description ?? "-"
      )
    }
  }
}
func records(_ provider: String, _ count: Int) -> [Data] {
  (0..<count).map { i in
    let stamp = String(
      format: "2026-11-%02dT%02d:%02d:%02dZ", i / 1440 % 7 + 1, i / 60 % 24, i % 60, i % 60)
    let text: String
    if provider == "codex" {
      switch i % 5 {
      case 0:
        text =
          "{\"type\":\"turn_context\",\"model\":\"m-\(i%3)\",\"effort\":\"high\",\"service_tier\":\"priority\"}"
      case 1:
        text =
          "{\"type\":\"function_call\",\"name\":\"mcp__workspace__synthetic\",\"arguments\":\"/skills/sample/SKILL.md\"}"
      case 2: text = "{\"type\":\"message\",\"role\":\"user\",\"text\":\"synthetic question\"}"
      case 3: text = "{\"type\":\"custom_tool_call\",\"name\":\"apply_patch\"}"
      default: text = "{\"kind\":\"ignored\"}"
      }
      return Data("{\"timestamp\":\"\(stamp)\",\"payload\":\(text)}".utf8)
    }
    if i % 3 == 0 {
      return Data(
        "{\"type\":\"user\",\"timestamp\":\"\(stamp)\",\"message\":{\"content\":\"synthetic\"}}"
          .utf8)
    }
    text =
      i % 3 == 1
      ? "{\"type\":\"tool_use\",\"name\":\"Skill\",\"input\":{\"skill\":\"sample\"},\"id\":\"t\(i)\"}"
      : "{\"type\":\"text\",\"text\":\"fixture\"}"
    return Data(
      "{\"type\":\"assistant\",\"timestamp\":\"\(stamp)\",\"message\":{\"id\":\"msg_\(i)\",\"model\":\"m-\(i%3)\",\"content\":[\(text)]}}"
        .utf8)
  }
}
func observe(_ provider: String, _ input: [Data]) -> String {
  var calendar = Calendar(identifier: .gregorian)
  calendar.timeZone = TimeZone(identifier: "Asia/Seoul")!
  let days = ActivityDayResolver(calendar: calendar)
  let file: FileActivity
  if provider == "codex" {
    var parser = CodexActivityParser(days: days)
    for line in input { parser.consume(line) }
    file = parser.finish()
  } else {
    var parser = ClaudeActivityParser(days: days)
    for line in input { parser.consume(line) }
    file = parser.finish()
  }
  var output: [String] = []
  for (day, stats) in file.days.sorted(by: { $0.key < $1.key }) {
    output.append(day.timeIntervalSinceReferenceDate.bitPattern.description)
    for group in [stats.messagesByModel, stats.tools, stats.skills] {
      output.append(
        group.sorted(by: { $0.key < $1.key }).map { "\($0.key)=\($0.value)" }.joined(separator: ",")
      )
    }
  }
  for group in [file.traits.models, file.traits.efforts, file.traits.speeds] {
    output.append(
      group.sorted(by: { $0.key < $1.key }).map { "\($0.key)=\($0.value)" }.joined(separator: ","))
  }
  return output.joined(separator: "|")
}


func codexRecordEdges() {
  let markers = [
    #""type":"session_meta""#, #""type":"thread_settings_applied""#, #""type":"turn_context""#,
    #""type":"function_call""#, #""type":"custom_tool_call""#, #""type":"message","role":"user""#,
  ]
  let fields = #""timestamp":"2026-11-01T00:35:44Z","id":"qa","model":"sample","effort":"high","service_tier":"priority","name":"tool","text":"question""#
  var calendar = Calendar(identifier: .gregorian)
  calendar.timeZone = TimeZone(identifier: "America/Los_Angeles")!
  let days = ActivityDayResolver(calendar: calendar)
  let message = Data((markers[5] + "," + fields).utf8)
  var groups: [[Data]] = []
  for first in markers {
    for second in markers {
      // Reordered markers in old and partially written records use the same precedence.
      let record = Data((first + "," + second + "," + fields).utf8)
      groups.append([record, message])
      groups.append([(Data([1, 2, 3]) + record).dropFirst(3), message])
    }
  }
  for count in [0, 1, 126, 127, 128, 129] {
    for name in [String(repeating: "a", count: count), "\\", "\\\u{301}", "도구"] {
      let path = "/skills/" + name + "/SKILL.md /skills/second/SKILL.md"
      groups.append([Data((markers[3] + "," + fields + " " + path).utf8)])
    }
  }
  let subagent = Data((markers[0] + #","id":"child","parent_thread_id":"parent""#).utf8)
  let detached = Data((markers[0] + #","id":"","parent_thread_id":"""#).utf8)
  let missingID = Data(markers[0].utf8)
  groups.append([subagent, message, missingID, message, detached, message])
  let invalidDate = String(repeating: "\\z", count: 2049)
  for marker in markers {
    groups.append([Data((marker + #","timestamp":"bad","name":""# + invalidDate + #"""#).utf8), message])
  }
  for (index, lines) in groups.enumerated() {
    var parser = CodexActivityParser(days: days)
    for line in lines { parser.consume(line) }
    let id = parser.rawSessionID.map { Data($0.utf8).base64EncodedString() } ?? "-"
    print("codexRecord", index, id, observe("codex", lines))
  }
}

func claudeToolBlocks() {
  let read = #"{"type":"tool_use","name":"Read","id":"same"}"#
  let skill = #"{"type":"tool_use","name":"Skill","input":{"skill":"analysis"}}"#
  let escaped = #"{"type":"tool_use","name":"S\u006bill","id":"e\u0301","input":{"skill":"a\\b"}}"#
  let duplicateKeys = #"{"type":"tool_use","name":"Read","name":"Skill","input":{"skill":"last"}}"#
  let blocks = [
    read, skill, escaped, duplicateKeys, #"{"type":"tool_use","name":""}"#,
    #"{"type":"tool_use","name":"Skill","id":"é","input":{"skill":false}}"#,
    #"{"type":"tool_use","name":"모형","id":"","input":[]}"#, #"{"type":"text","text":"ignored"}"#,
    "null", "3", "[]",
  ]
  let message = Data(#"{"type":"user","timestamp":"2026-11-01T00:30:00Z","message":{"content":"sample"}}"#.utf8)
  for (i, first) in blocks.enumerated() {
    for (j, second) in blocks.enumerated() {
      var lines = [message]
      for sidechain in [true, false] {
        let json = #"{"type":"assistant","timestamp":"2026-11-02T03:14:00Z","isSidechain":\#(sidechain),"message":{"id":"msg_q","model":"sample","content":[\#(first),\#(second),\#(read),\#(skill)]}}"#
        let padded = Data([0, 1]) + Data(json.utf8)
        lines.append(padded.dropFirst(2))
      }
      print("claudeToolBlocks", i, j, observe("claude", lines))
    }
  }
}

func dayActivityCopies() {
  let day = Date(timeIntervalSinceReferenceDate: 0)
  let next = Date(timeIntervalSinceReferenceDate: 86_400)
  var first = FileActivity()
  first.edit(day) { $0.tools["same"] = 1 }
  var second = first
  second.edit(day) { value in
    value.tools["same", default: 0] += 9
    value.skills["sample"] = 7
  }
  first.edit(next) { _ in }
  let saved = second
  second.edit(day) { $0 = DayActivity() }
  for value in [first, second, saved] {
    print("dayCopy", value.days.count, value.days[next] != nil,
          value.days[day]?.tools["same"] ?? -1, value.days[day]?.skills["sample"] ?? -1)
  }
}

if CommandLine.arguments.contains("--bench") {
  for provider in ["codex", "claude"] {
    for count in [60, 1440, 10080] {
      let lines = records(provider, count)
      let start = DispatchTime.now().uptimeNanoseconds
      let result = observe(provider, lines)
      print(
        provider, count, Data(result.utf8).base64EncodedString(),
        DispatchTime.now().uptimeNanoseconds - start)
    }
  }
} else {
  edgeCases()
  dates()
  codexRecordEdges()
  claudeToolBlocks()
  dayActivityCopies()
  for provider in ["codex", "claude"] {
    print(provider, observe(provider, records(provider, 10080)))
  }
}
