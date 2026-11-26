import CodeRimUsageCore
import Foundation
import Darwin

// Every row exercises the production CostEstimator with a real PricingCatalog.
let now = Date(timeIntervalSince1970: 1_700_000_000)
func d(_ text: String) -> Decimal { Decimal(string: text, locale: Locale(identifier: "en_US_POSIX"))! }
func usage(_ input: Int64, _ cached: Int64, _ written: Int64?, _ output: Int64) -> TokenUsage {
    TokenUsage(inputTokens: input, cachedInputTokens: cached, cacheWriteInputTokens: written, outputTokens: output)
}
func sample(_ id: String, _ total: TokenUsage, _ high: TokenUsage = .zero, unknown: Bool = false) -> ModelTokenUsageSample {
    ModelTokenUsageSample(modelID: id, usage: total, highContextUsage: high, hasUnknownPricingContext: unknown, occurredAt: now)
}
func price(_ id: String, _ input: String, _ cached: String, _ output: String, _ write: String?,
           _ highInput: String?, _ highOutput: String?) -> ModelPricing {
    ModelPricing(modelID: id, inputUSDPerMillionTokens: d(input), cachedInputUSDPerMillionTokens: d(cached),
                 outputUSDPerMillionTokens: d(output), cacheWriteInputMultiplier: write.map(d),
                 highContextInputMultiplier: highInput.map(d), highContextOutputMultiplier: highOutput.map(d))
}
let catalog = try PricingCatalog(metadata: PricingCatalogMetadata(version:"fixture-v2", retrievedAt:now), prices:[
    price(" CostlyΩ ", "0.123456789123456789123456789123456789","0.00000000000000347","89.01234567890123456789",
          "1.200000000000000000000000000000001","3.000000000000000000000001","0.0078125"),
    price("gpt-5.6-sol","1000","0","0.25",nil,nil,"2"),
    price("zero","0","0","0","0","0",nil),
    price("extreme","1e120","1e-120","8.75e118","1e20","1e20","0"),
    price("no-write","1.1","0.1","0.1",nil,nil,nil)
])
let custom = CostEstimator(catalog: catalog)
let current = CostEstimator()
let common = usage(100,25,12,90)
let high = usage(20,3,1,16)
let invalid = usage(2,2,1,0)
let incomplete = sample("COSTLYΩ",usage(4,0,nil,0))
let absent = sample("  AbSeNt\n",common)
let bad = sample("unknown",invalid)
let alias = sample("\n GPT-5.6 ",common,high,unknown:true)

func amount(_ value: Decimal?) -> String? {
    guard var decimal = value else { return nil }
    return NSDecimalString(&decimal, Locale(identifier:"en_US_POSIX"))
}
struct Observed: Encodable {
    let label: String
    let amount: String?
    let breakdown: [String?]?
    let coverage: String
    let identifiers: [String]
    let version: String
    let retrievedAt: TimeInterval
}
var results: [Observed] = []
@MainActor func record(_ label:String,_ estimator:CostEstimator,_ samples:[ModelTokenUsageSample]) {
    let result=estimator.estimate(samples)
    let status:String
    let ids:[String]
    switch result.coverage {
    case .complete: status="complete"; ids=[]
    case .invalidTokenUsage: status="invalid"; ids=[]
    case .unavailable(let missing): status="missing"; ids=missing
    case .incompleteMetadata(let incomplete): status="incomplete"; ids=incomplete
    }
    let parts=result.breakdown.map{[amount($0.uncachedInputUSD),amount($0.cachedInputUSD),amount($0.cacheWriteInputUSD),amount($0.outputUSD)]}
    results.append(Observed(label:label,amount:amount(result.amountUSD),breakdown:parts,coverage:status,identifiers:ids,
                            version:result.pricingBasis.catalogVersion,retrievedAt:result.pricingBasis.catalogRetrievedAt.timeIntervalSince1970))
}
record("empty",custom,[])
record("alias custom",custom,[alias])
record("known one",current,[sample("gpt-6-astra",common,high)])
record("decimal precision",custom,[sample("costlyω",common,high)])
record("per-sample order",custom,[sample("costlyω",common,high),alias,sample("costlyω",common,high)])
record("Unpriced old usage",custom,[absent, sample(" \t",common),absent])
record("partial and missing",custom,[incomplete,absent])
record("invalid later",custom,[incomplete,absent,bad])
record("invalid first",custom,[bad,absent,incomplete])
record("incomplete model list",custom,[incomplete,incomplete,sample(" zero ",common,high,unknown:true)])
record("valid subset negative residual",custom,[sample("costlyω",usage(100,90,0,0),usage(50,0,0,0))])
record("unknown write usage, unpriced write",custom,[sample("no-write",usage(85,3,nil,9),usage(10,1,nil,2))])
record("zero with unknown writes",custom,[sample("costlyω",usage(0,0,nil,0))])
record("known zero outranks unknown high write",custom,[sample("costlyω",common,usage(0,0,nil,0))])
record("invalid high component",current,[sample("gpt-6-astra",common,usage(1,0,2,0))])
record("high larger than total",current,[sample("gpt-6-astra",common,usage(101,0,0,0))])
record("sums overflow invalid",custom,[sample("no-write",usage(Int64.max,Int64.max,1,Int64.max))])
record("negative signed input",custom,[sample("no-write",usage(.min,0,nil,0))])
record("huge counts",custom,[sample("costlyω",usage(.max,0,0,.max),usage(42,0,0,7))])
record("decimal overflow",custom,[sample("extreme",usage(.max,0,1,.max),usage(5,0,1,1))])
record("tiny rates",custom,[sample("extreme",usage(100,100,0,0))])
for id in PricingCatalog.current.supportedModelIDs+["GPT-5.6", "unknown"] {
    for total in [TokenUsage.zero, common,usage(9,1,nil,3),usage(.max,17,23,.max)] {
        for ctx in [TokenUsage.zero, high,usage(5,5,nil,0)] {
            for unknown in [false,true] {
                record("bundled-\(results.count)",current,[sample(id,total,ctx,unknown:unknown)])
            }
        }
    }
}
// Mix component boundaries, independent high-context quantities and unequal
// rates. Every input is deterministic and lives solely in this process.
var seed: UInt64 = 20261126
@MainActor func next(_ maximum: Int64) -> Int64 {
    seed = seed &* 6364136223846793005 &+ 1442695040888963407
    return Int64((seed >> 16) % UInt64(maximum + 1))
}
var benchmarkSamples: [ModelTokenUsageSample] = []
for index in 0..<512 {
    let input=next(2_000_000)
    let cached=next(input)
    let written=next(input-cached)
    let output=next(535_000)
    let highInput=next(input)
    let highCached=next(min(cached,highInput))
    let highWritten=next(min(written,highInput-highCached))
    let total=usage(input,cached,written,output)
    let hi=usage(highInput,highCached,highWritten,next(output))
    let id = index % 3 == 0 ? "gpt-5.6" : "cosTLyΩ"
    let row=sample(id,total,hi)
    record("mixed-\(index)",custom,[row])
    benchmarkSamples.append(row)
}
record("mixed aggregate",custom,benchmarkSamples)
if CommandLine.arguments.contains("--benchmark") {
    let cpuStart=clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID)
    let begin=DispatchTime.now().uptimeNanoseconds
    var checksum=Decimal.zero
    for _ in 0..<12 {
        checksum += custom.estimate(benchmarkSamples).amountUSD!
    }
    let elapsed=DispatchTime.now().uptimeNanoseconds - begin
    let cpu=clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID)-cpuStart
    var resources = rusage()
    getrusage(RUSAGE_SELF,&resources)
    let observation: [String: String] = [
        "cpuNanoseconds": String(cpu), "peakRssBytes": String(resources.ru_maxrss),
        "nanoseconds": String(elapsed), "samples": String(12 * benchmarkSamples.count),
        "checksum": amount(checksum)!
    ]
    let data=try JSONEncoder().encode(observation)
    try FileHandle.standardOutput.write(contentsOf:data)
    exit(0)
}
let encoded=JSONEncoder()
encoded.outputFormatting=[.sortedKeys]
try FileHandle.standardOutput.write(contentsOf: encoded.encode(results))
