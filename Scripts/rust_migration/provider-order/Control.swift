// Benchmark control: feed the same host-generated ordinal IDs into Swift queues.
func cr_arrange_providers(
    _ items: [UInt16], _ itemCount: UInt16, _ preferences: [UInt16], _ preferenceCount: UInt16,
    _ groupCount: UInt16, _ positions: inout [UInt16]
) -> UInt8 {
    let end = UInt16.max, consumed = UInt16.max - 1
    var heads = [UInt16](repeating: end, count: Int(groupCount))
    var next = [UInt16](repeating: end, count: items.count)
    for i in items.indices.reversed() where items[i] != end {
        let group = Int(items[i])
        next[i] = heads[group]
        heads[group] = UInt16(i)
    }
    var count = 0
    for id in preferences {
        let group = Int(id)
        let first = heads[group]
        if first != end {
            heads[group] = next[Int(first)]
            next[Int(first)] = consumed
            positions[count] = first
            count += 1
        }
    }
    for i in next.indices where next[i] != consumed {
        positions[count] = UInt16(i)
        count += 1
    }
    return 1
}
