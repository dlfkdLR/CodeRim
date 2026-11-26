import CodeRimUsageCore
import Darwin
import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

struct Vector: Decodable {
    let name: String
    let data: Data
    let group: String?
}
struct Observation: Encodable {
    let message, session, model: String
    let agent, cwd: String?
    let timestamp: UInt64
    let input, cached, output: Int64
    let written: Int64?
    init(_ value: ClaudeUsageObservation) {
        message=value.messageID; session=value.sessionID; model=value.model
        agent=value.agentID; cwd=value.workingDirectory
        timestamp=value.occurredAt.timeIntervalSinceReferenceDate.bitPattern
        input=value.usage.inputTokens; cached=value.usage.cachedInputTokens
        output=value.usage.outputTokens; written=value.usage.cacheWriteInputTokens
    }
}
struct Row: Encodable {
    let name: String
    let observation: Observation?
    let isRejectedAccounting: Bool
    let rejectedAt, timestamp: UInt64?
    let route: Int?
}
let vectors=try JSONDecoder().decode([Vector].self, from: Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1])))
let parser=ClaudeJSONLParser()
let encoder=JSONEncoder()
encoder.outputFormatting=[.sortedKeys]
// Exercise the app's normal in-memory importer with synthetic URLs and a
// deterministic project key. No provider directory or account store is opened.
struct ImportedEvent: Encodable {
    var key, source: String
    var session, parent, model, project: String?
    var time: UInt64
    var position: Int64
    var usage: TokenUsage
}
struct Evidence: Encodable { let source: String; let position: Int64; let time: UInt64 }
struct ImportResult: Encodable {
    var filename: String
    var session, parent, project, projectName, model: String?
    var start: UInt64?
    var evidence: [Evidence]
    var events: [ImportedEvent]
    var excluded: [String]
}
if CommandLine.arguments.last == "--importer" {
    let names=["canonical","absent caches","agentId null","input_tokens-1","ignore user","duplicate first valid",
               "date '2001-01-01T00:00:00.000000001Z'"]
    var imports:[ImportResult]=[]
    for filename in ["session.jsonl","agent-worker.jsonl"] {
        let url=URL(fileURLWithPath:"/private/tmp/synthetic-coderim-claude/"+filename)
        var session=SourceImportSession(checkpoint:.fresh(sourcePath:url.path,fileIdentity:"fixture"),state:.empty,
            provider:.claude,sourceURL:url,importCutoff:Date(timeIntervalSince1970:1_100_000_000),
            projectKey:Data(repeating:3,count:32),loadState:{_ in .empty})
        for (index,name) in names.enumerated() {
            guard let row=vectors.first(where:{$0.name==name}) else { fatalError("missing fixture "+name) }
            try await session.consume(row.data,position:Int64(index*512))
        }
        let checkpoint=session.checkpoint
        imports.append(ImportResult(filename:filename,session:checkpoint.sessionID,parent:checkpoint.parentSessionID,
            project:checkpoint.projectPath,projectName:checkpoint.projectName,model:checkpoint.model,
            start:checkpoint.sessionStartedAt?.timeIntervalSinceReferenceDate.bitPattern,
            evidence:session.pendingEvidence.map{Evidence(source:$0.source,position:$0.position,time:$0.observedAt.timeIntervalSinceReferenceDate.bitPattern)},
            events:session.pendingEvents.map{ImportedEvent(key:$0.eventKey,source:$0.sourcePath,session:$0.sessionID,parent:$0.importParentSessionID,
                model:$0.model,project:$0.projectPath,time:$0.occurredAt.timeIntervalSinceReferenceDate.bitPattern,
                position:$0.sourcePosition,usage:$0.usage)},
            excluded:session.excludedEventKeys.sorted()))
    }
    try FileHandle.standardOutput.write(contentsOf:encoder.encode(imports))
    exit(0)
}
if CommandLine.arguments.count > 2 {
    let group=CommandLine.arguments[2]
    let chosen=vectors.filter { $0.group==group }
    precondition(!chosen.isEmpty)
    let rounds=4000
    var checksum: UInt64=0
    let cpuStart=clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID)
    let start=DispatchTime.now().uptimeNanoseconds
    for i in 0..<rounds {
        if let o=parser.parse(chosen[i % chosen.count].data) {
            checksum &+= UInt64(o.messageID.utf8.count+o.sessionID.utf8.count)
            checksum &+= UInt64(o.usage.totalTokens)
            checksum &+= o.occurredAt.timeIntervalSinceReferenceDate.bitPattern
        } else { checksum &+= 1 }
    }
    let elapsed=DispatchTime.now().uptimeNanoseconds-start
    let cpu=clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID)-cpuStart
    var resource=rusage()
    getrusage(RUSAGE_SELF,&resource)
    try FileHandle.standardOutput.write(contentsOf:encoder.encode([
        "nanoseconds":elapsed,"cpuNanoseconds":cpu,"peakRssBytes":UInt64(resource.ru_maxrss),
        "rows":UInt64(rounds),"checksum":checksum
    ]))
} else {
    let values=vectors.map { v -> Row in
        let o=parser.parse(v.data)
        let rejection = o == nil ? ClaudeJSONLParser.rejectedAccounting(v.data) : (false,nil)
#if CODERIM_RUST_ACCOUNTING
        let route:Int?=v.data.withUnsafeBytes { (buffer:UnsafeRawBufferPointer) in
            let bytes=buffer.bindMemory(to:UInt8.self)
            return Int(cr_parse_claude_usage(bytes.baseAddress,bytes.count).kind)
        }
#else
        let route:Int?=nil
#endif
        return Row(name:v.name,observation:o.map(Observation.init),isRejectedAccounting:rejection.0,
                   rejectedAt:rejection.1?.timeIntervalSinceReferenceDate.bitPattern,
                   timestamp:ClaudeJSONLParser.timestamp(of:v.data)?.timeIntervalSinceReferenceDate.bitPattern,route:route)
    }
    try FileHandle.standardOutput.write(contentsOf:encoder.encode(values))
}
