import Darwin
import Foundation

// The only live pid in the synthetic registry is this fixture process.
struct Step: Decodable {
  let kind: String
  let path: String?
  let text: String?
  let stamp: Int?
  let offset: Double?
}
let root = URL(fileURLWithPath: CommandLine.arguments[1])
let registry = root.appendingPathComponent("sessions")
let projects = root.appendingPathComponent("projects")
let now = Date(timeIntervalSince1970: 2_000_000_000)
let manager = FileManager.default
try manager.createDirectory(at: registry, withIntermediateDirectories: true)
try manager.createDirectory(at: projects, withIntermediateDirectories: true)
var reader = ClaudeTranscriptReader(projects: projects)
@MainActor func read(_ offset: Double = 0) -> [AgentSession] {
  ClaudeSessionMonitor.read(
    directory: registry, transcripts: reader, dropIdleAfter: 90,
    now: now.addingTimeInterval(offset))
}
if CommandLine.arguments.count == 5 {
  let count = Int(CommandLine.arguments[2])!
  let shape = CommandLine.arguments[3]
  let rounds = Int(CommandLine.arguments[4])!
  let project = projects.appendingPathComponent("-fixture")
  try manager.createDirectory(at: project, withIntermediateDirectories: true)
  for index in 0..<count {
    let id = "test-\(index)"
    let reg: [String: Any] = [
      "pid": getpid(), "cwd": "/fixture", "sessionId": id,
      "statusUpdatedAt": 2_000_000_000_000, "name": id,
    ]
    let bytes = try JSONSerialization.data(withJSONObject: reg)
    try bytes.write(to: registry.appendingPathComponent(id + ".json"))
    if shape != "missing" {
      let turn = shape == "active" || shape == "queued" ? "user" : "continued-in"
      var lines = "{\"type\":\"\(turn)\",\"timestamp\":\"2033-05-18T03:33:20.000Z\"}\n"
      if shape == "queued" {
        lines += String(
          repeating:
            "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"2033-05-18T03:33:23.000Z\"}\n",
          count: 220)
      }
      try Data(lines.utf8).write(to: project.appendingPathComponent(id + ".jsonl"))
    }
  }
  var delivered = read().count
  let start = clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID)
  for _ in 0..<rounds { delivered += read().count }
  print((clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID) - start) / UInt64(rounds), delivered)
} else {
  let input = URL(fileURLWithPath: CommandLine.arguments[2])
  let steps = try JSONDecoder().decode([Step].self, from: Data(contentsOf: input))
  func fields(_ s: AgentSession) -> [String: Any] {
    var values: [String: Any] = [
      "id": s.id.replacingOccurrences(of: "claude.\(getpid())", with: "claude.QA-PID"),
      "name": s.name, "detail": s.detail, "state": String(describing: s.state),
      "since": String(s.since.timeIntervalSinceReferenceDate.bitPattern),
      "ourPID": s.processID == getpid(),
      "followUps": s.followUps, "toolCalls": s.toolCalls, "compacting": s.isCompacting,
    ]
    values["waiting"] = s.waitingFor
    values["thread"] = s.codexThreadID
    values["parentId"] = s.parentThread?.id
    values["parentTitle"] = s.parentThread?.title
    values["workingDirectory"] = s.workingDirectory
    values["project"] = s.projectKey
    values["transcript"] = s.transcriptID
    values["usageSessionID"] = s.usageSessionID
    values["host"] = s.remoteHostID
    if let m = s.details {
      values["model"] = m.model
      values["effort"] = m.effort
      values["fast"] = m.fast
      values["daybreak"] = m.daybreak
    }
    return values
  }
  var outputs = [[[String: Any]]]()
  for step in steps {
    let url = root.appendingPathComponent(step.path ?? "unused")
    let bytes = Data(
      (step.text ?? "").replacingOccurrences(of: "__PID__", with: String(getpid())).utf8)
    switch step.kind {
    case "write", "replace", "append":
      try manager.createDirectory(
        at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
      if step.kind == "append" {
        let handle = try FileHandle(forWritingTo: url)
        try handle.seekToEnd()
        try handle.write(contentsOf: bytes)
        try handle.close()
      } else {
        try bytes.write(to: url, options: step.kind == "replace" ? .atomic : [])
      }
    case "remove": try manager.removeItem(at: url)
    case "link":
      try manager.createSymbolicLink(
        atPath: url.path, withDestinationPath: root.appendingPathComponent(step.text!).path)
    case "directory": try manager.createDirectory(at: url, withIntermediateDirectories: true)
    case "deny": try manager.setAttributes([.posixPermissions: 0], ofItemAtPath: url.path)
    case "restore": try manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    case "restart": reader = ClaudeTranscriptReader(projects: projects)
    case "poll": break
    default: fatalError("Unknown synthetic step")
    }
    if url.lastPathComponent.hasSuffix(".meta.json"), step.kind == "write" || step.kind == "replace"
    {
      try manager.setAttributes(
        [.creationDate: Date(timeIntervalSince1970: 1_000_000_000)], ofItemAtPath: url.path)
    }
    if let n = step.stamp {
      var times = [
        timespec(tv_sec: 2_000_000_000, tv_nsec: n), timespec(tv_sec: 2_000_000_000, tv_nsec: n),
      ]
      precondition(utimensat(AT_FDCWD, url.path, &times, AT_SYMLINK_NOFOLLOW) == 0)
    }
    outputs.append(read(step.offset ?? 0).map(fields))
  }
  let data = try JSONSerialization.data(withJSONObject: outputs, options: [.sortedKeys])
  print(String(decoding: data, as: UTF8.self))
}
