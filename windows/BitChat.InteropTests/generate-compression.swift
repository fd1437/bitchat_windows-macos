import Foundation
import Compression
import CryptoKit
var fixtures:[[String:String]]=[]
for count in [32,256,300,512,1024,4000] {
 let data=Data(String(repeating:"hello bitchat mesh! ",count:count/20+1).utf8).prefix(count)
 let target=UnsafeMutablePointer<UInt8>.allocate(capacity:data.count*2+16)
 let n=data.withUnsafeBytes { p in compression_encode_buffer(target,data.count*2+16,p.bindMemory(to:UInt8.self).baseAddress!,data.count,nil,COMPRESSION_ZLIB) }
 let compressed=Data(bytes:target,count:n);target.deallocate()
 fixtures.append(["raw":data.base64EncodedString(),"deflate":compressed.base64EncodedString()])
}
let out=try JSONSerialization.data(withJSONObject:fixtures,options:[.prettyPrinted,.sortedKeys]);try out.write(to:URL(fileURLWithPath:CommandLine.arguments[1]))
