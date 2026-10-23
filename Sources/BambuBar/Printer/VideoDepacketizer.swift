import CoreMedia
import Foundation

/// Turns RTP payloads (RFC 6184 for H.264, RFC 7798 for H.265) into whole frames, and frames into
/// `CMSampleBuffer`s that `AVSampleBufferDisplayLayer` can decode.
///
/// Parameter sets come from the SDP and from the stream itself; a new format description is made whenever
/// they change. Frames before the first keyframe are dropped.
final class VideoDepacketizer {
    enum Codec: String {
        case h264 = "H.264"
        case h265 = "H.265"
    }

    struct Frame {
        var sample: CMSampleBuffer
        var isKeyframe: Bool
    }

    let codec: Codec
    private(set) var format: CMVideoFormatDescription?

    private var vps: [UInt8]?
    private var sps: [UInt8]?
    private var pps: [UInt8]?
    private var formatSets: [[UInt8]] = []

    private var nals: [[UInt8]] = []
    private var isKeyframe = false
    private var damaged = false
    private var fragment: [UInt8]?
    private var timestamp: UInt32?
    private var lastSequence: UInt16?
    private var sawKeyframe = false

    init(codec: Codec, parameterSets: [[UInt8]]) {
        self.codec = codec
        for nal in parameterSets { _ = takeParameterSet(nal) }
    }

    /// One RTP packet's payload. Returns the frames it completed (usually none or one).
    func push(_ payload: [UInt8], sequence: UInt16, timestamp: UInt32, marker: Bool) -> [Frame] {
        var frames: [Frame] = []
        if let last = lastSequence, sequence != last &+ 1 {
            // A lost packet spoils the frame being assembled.
            damaged = true
            fragment = nil
        }
        lastSequence = sequence
        if let current = self.timestamp, current != timestamp, let frame = flush() { frames.append(frame) }
        self.timestamp = timestamp
        guard !payload.isEmpty else { return frames }
        switch codec {
        case .h264: unpackH264(payload)
        case .h265: unpackH265(payload)
        }
        if marker, let frame = flush() { frames.append(frame) }
        return frames
    }

    // MARK: - Payloads

    private func unpackH264(_ p: [UInt8]) {
        let type = p[0] & 0x1F
        switch type {
        case 1...23:
            add(p)
        case 24:  // STAP-A: 16-bit sizes, each followed by a NAL unit
            unpackAggregate(p, from: 1)
        case 28:  // FU-A
            guard p.count > 2 else { return }
            let header = p[1]
            if header & 0x80 != 0 {
                fragment = [(p[0] & 0xE0) | (header & 0x1F)] + p[2...]
            } else if fragment != nil {
                fragment! += p[2...]
            }
            if header & 0x40 != 0, let nal = fragment {
                fragment = nil
                add(nal)
            }
        default:
            break
        }
    }

    private func unpackH265(_ p: [UInt8]) {
        guard p.count > 2 else { return }
        let type = (p[0] >> 1) & 0x3F
        switch type {
        case 0..<48:
            add(p)
        case 48:  // aggregation packet
            unpackAggregate(p, from: 2)
        case 49:  // fragmentation unit
            guard p.count > 3 else { return }
            let header = p[2]
            if header & 0x80 != 0 {
                fragment = [(p[0] & 0x81) | ((header & 0x3F) << 1), p[1]] + p[3...]
            } else if fragment != nil {
                fragment! += p[3...]
            }
            if header & 0x40 != 0, let nal = fragment {
                fragment = nil
                add(nal)
            }
        default:
            break
        }
    }

    private func unpackAggregate(_ p: [UInt8], from start: Int) {
        var i = start
        while i + 2 <= p.count {
            let size = Int(p[i]) << 8 | Int(p[i + 1])
            i += 2
            guard size > 0, i + size <= p.count else { return }
            add(Array(p[i..<(i + size)]))
            i += size
        }
    }

    // MARK: - NAL units

    private func add(_ nal: [UInt8]) {
        guard !nal.isEmpty, !takeParameterSet(nal) else { return }
        switch codec {
        case .h264:
            let type = nal[0] & 0x1F
            if type == 9 { return }  // access unit delimiter
            if type == 5 { isKeyframe = true }
        case .h265:
            let type = (nal[0] >> 1) & 0x3F
            if type == 35 { return }  // access unit delimiter
            if (16...21).contains(type) { isKeyframe = true }
        }
        nals.append(nal)
    }

    /// Keeps SPS/PPS (and VPS) aside instead of in the frame. Returns whether `nal` was one.
    private func takeParameterSet(_ nal: [UInt8]) -> Bool {
        guard !nal.isEmpty else { return false }
        switch codec {
        case .h264:
            switch nal[0] & 0x1F {
            case 7: sps = nal
            case 8: pps = nal
            default: return false
            }
        case .h265:
            switch (nal[0] >> 1) & 0x3F {
            case 32: vps = nal
            case 33: sps = nal
            case 34: pps = nal
            default: return false
            }
        }
        return true
    }

    // MARK: - Frames

    private func flush() -> Frame? {
        defer {
            nals = []
            isKeyframe = false
            damaged = false
        }
        guard !nals.isEmpty, !damaged else { return nil }
        if isKeyframe {
            updateFormat()
            sawKeyframe = format != nil
        }
        guard sawKeyframe, let format else { return nil }
        guard let sample = makeSample(nals, format: format, keyframe: isKeyframe) else { return nil }
        return Frame(sample: sample, isKeyframe: isKeyframe)
    }

    private func updateFormat() {
        let sets: [[UInt8]]
        switch codec {
        case .h264:
            guard let sps, let pps else { return }
            sets = [sps, pps]
        case .h265:
            guard let vps, let sps, let pps else { return }
            sets = [vps, sps, pps]
        }
        guard sets != formatSets || format == nil else { return }
        let buffers = sets.map { set -> UnsafeMutablePointer<UInt8> in
            let pointer = UnsafeMutablePointer<UInt8>.allocate(capacity: set.count)
            pointer.initialize(from: set, count: set.count)
            return pointer
        }
        defer { buffers.forEach { $0.deallocate() } }
        let pointers = buffers.map { UnsafePointer($0) }
        let sizes = sets.map(\.count)
        var description: CMFormatDescription?
        let status: OSStatus
        switch codec {
        case .h264:
            status = CMVideoFormatDescriptionCreateFromH264ParameterSets(
                allocator: kCFAllocatorDefault, parameterSetCount: sets.count, parameterSetPointers: pointers,
                parameterSetSizes: sizes, nalUnitHeaderLength: 4, formatDescriptionOut: &description)
        case .h265:
            status = CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                allocator: kCFAllocatorDefault, parameterSetCount: sets.count, parameterSetPointers: pointers,
                parameterSetSizes: sizes, nalUnitHeaderLength: 4, extensions: nil, formatDescriptionOut: &description)
        }
        guard status == noErr, let description else { return }
        format = description
        formatSets = sets
    }

    /// Length-prefixed (AVCC/HVCC) NAL units in one sample, shown as soon as it's decoded.
    private func makeSample(_ nals: [[UInt8]], format: CMVideoFormatDescription, keyframe: Bool) -> CMSampleBuffer? {
        var bytes: [UInt8] = []
        bytes.reserveCapacity(nals.reduce(0) { $0 + $1.count + 4 })
        for nal in nals {
            let length = UInt32(nal.count).bigEndian
            withUnsafeBytes(of: length) { bytes.append(contentsOf: $0) }
            bytes.append(contentsOf: nal)
        }
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: bytes.count, blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil, offsetToData: 0, dataLength: bytes.count, flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &block) == noErr, let block,
            CMBlockBufferReplaceDataBytes(with: bytes, blockBuffer: block, offsetIntoDestination: 0, dataLength: bytes.count) == noErr
        else { return nil }
        var sample: CMSampleBuffer?
        var size = bytes.count
        guard CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format, sampleCount: 1,
            sampleTimingEntryCount: 0, sampleTimingArray: nil, sampleSizeEntryCount: 1, sampleSizeArray: &size,
            sampleBufferOut: &sample) == noErr, let sample
        else { return nil }
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true),
           CFArrayGetCount(attachments) > 0 {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dict, Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                 Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
            if !keyframe {
                CFDictionarySetValue(dict, Unmanaged.passUnretained(kCMSampleAttachmentKey_NotSync).toOpaque(),
                                     Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
            }
        }
        return sample
    }
}

extension CMVideoFormatDescription {
    var dimensionsText: String {
        let size = CMVideoFormatDescriptionGetDimensions(self)
        return "\(size.width)×\(size.height)"
    }
}
