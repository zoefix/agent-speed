import Darwin
import Foundation

/// The regular files of one folder with their modification times, listed in bulk (`getattrlistbulk`), so a cache of
/// ≈ 83 000 entries costs one pass instead of a `stat` per file (QUOTAS 4.4). Falls back to `readdir` and `fstatat`.
enum CacheFolderListing {
    struct File: Equatable {
        var name: String
        var modifiedMs: Int64
    }

    static func files(in folder: String, where wanted: (String) -> Bool) -> [File]? {
        let descriptor = open(folder, O_RDONLY | O_DIRECTORY | O_CLOEXEC)
        guard descriptor >= 0 else { return nil }
        defer { close(descriptor) }
        return bulk(descriptor, wanted) ?? plain(descriptor, wanted)
    }

    private static func bulk(_ descriptor: Int32, _ wanted: (String) -> Bool) -> [File]? {
        var request = attrlist()
        request.bitmapcount = u_short(ATTR_BIT_MAP_COUNT)
        request.commonattr = attrgroup_t(ATTR_CMN_RETURNED_ATTRS) | attrgroup_t(ATTR_CMN_NAME)
            | attrgroup_t(ATTR_CMN_OBJTYPE) | attrgroup_t(ATTR_CMN_MODTIME)
        let capacity = 256 * 1024
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: capacity, alignment: 16)
        defer { buffer.deallocate() }
        var files: [File] = []
        while true {
            let count = getattrlistbulk(descriptor, &request, buffer, capacity, 0)
            if count < 0 { return files.isEmpty ? nil : files }
            if count == 0 { return files }
            var entry = buffer
            for _ in 0..<count {
                let length = Int(entry.loadUnaligned(as: UInt32.self))
                defer { entry += length }
                var field = entry + 4
                let returned = field.loadUnaligned(as: attribute_set_t.self)
                field += MemoryLayout<attribute_set_t>.size
                var name: String?
                if returned.commonattr & attrgroup_t(ATTR_CMN_NAME) != 0 {
                    let offset = Int(field.loadUnaligned(as: Int32.self))
                    let nameLength = Int(field.loadUnaligned(fromByteOffset: 4, as: UInt32.self))
                    if nameLength > 0 {
                        let start = (field + offset).assumingMemoryBound(to: CChar.self)
                        name = String(cString: start)
                    }
                    field += MemoryLayout<attrreference_t>.size
                }
                var type = fsobj_type_t(VNON.rawValue)
                if returned.commonattr & attrgroup_t(ATTR_CMN_OBJTYPE) != 0 {
                    type = field.loadUnaligned(as: fsobj_type_t.self)
                    field += MemoryLayout<fsobj_type_t>.size
                }
                var modified: Int64 = 0
                if returned.commonattr & attrgroup_t(ATTR_CMN_MODTIME) != 0 {
                    let seconds = field.loadUnaligned(as: Int.self)
                    let nanoseconds = field.loadUnaligned(fromByteOffset: MemoryLayout<Int>.size, as: Int.self)
                    modified = Int64(seconds) * 1000 + Int64(nanoseconds / 1_000_000)
                }
                guard let name, type == fsobj_type_t(VREG.rawValue), wanted(name) else { continue }
                files.append(File(name: name, modifiedMs: modified))
            }
        }
    }

    private static func plain(_ descriptor: Int32, _ wanted: (String) -> Bool) -> [File]? {
        let copy = dup(descriptor)
        guard copy >= 0, let directory = fdopendir(copy) else { return nil }
        defer { closedir(directory) }
        var files: [File] = []
        while let item = readdir(directory) {
            let name = withUnsafeBytes(of: item.pointee.d_name) { raw in
                String(cString: raw.bindMemory(to: CChar.self).baseAddress!)
            }
            guard wanted(name) else { continue }
            var info = stat()
            guard fstatat(descriptor, name, &info, AT_SYMLINK_NOFOLLOW) == 0, info.st_mode & S_IFMT == S_IFREG else { continue }
            files.append(File(name: name, modifiedMs: Int64(info.st_mtimespec.tv_sec) * 1000
                                + Int64(info.st_mtimespec.tv_nsec / 1_000_000)))
        }
        return files
    }
}
