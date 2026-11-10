// Encode recorded, isolated screen frames. Node built-ins only; no app dependency.
const fs=require('node:fs'),path=require('node:path'),zlib=require('node:zlib');
const [root,output]=process.argv.slice(2);if(!root||!output)throw Error('Usage: node Make-MotionPreview.cjs frame-directory output.png');
const files=fs.readdirSync(root).filter(n=>/^screen-\d+\.png$/.test(n)).sort();
const trace=JSON.parse(fs.readFileSync(path.join(root,'motion-visibility.json'),'utf8')).rows.filter(r=>r.cycle===0);
function chunks(file){const b=fs.readFileSync(file),out=[];for(let n=8;n<b.length;){const len=b.readUInt32BE(n);out.push({type:b.toString('ascii',n+4,n+8),data:b.subarray(n+8,n+8+len)});n+=len+12;}return out;}
const frames=files.map(n=>chunks(path.join(root,n))),header=frames[0].find(c=>c.type==='IHDR').data;
const width=header.readUInt32BE(0),height=header.readUInt32BE(4);
const crcTable=Array.from({length:256},(_,i)=>{let c=i;for(let k=0;k<8;k++)c=(c&1)?0xedb88320^(c>>>1):c>>>1;return c>>>0;});
function chunk(type,data){const name=Buffer.from(type),body=Buffer.concat([name,data]);let crc=0xffffffff;for(const x of body)crc=crcTable[(crc^x)&255]^(crc>>>8);const out=Buffer.alloc(data.length+12);out.writeUInt32BE(data.length);body.copy(out,4);out.writeUInt32BE((crc^0xffffffff)>>>0,out.length-4);return out;}
const actl=Buffer.alloc(8);actl.writeUInt32BE(frames.length);
const png=[Buffer.from([137,80,78,71,13,10,26,10]),chunk('IHDR',header),chunk('acTL',actl)];let sequence=0;
const delays=frames.map((_,i)=>i===frames.length-1?850:Math.max(20,Math.min(150,(trace[i+1]?.ms??trace[i].ms+30)-trace[i].ms)));
for(let i=0;i<frames.length;i++){
 if(!frames[i].find(c=>c.type==='IHDR').data.equals(header))throw Error('Frame format changed');
 const ctl=Buffer.alloc(26);ctl.writeUInt32BE(sequence++);ctl.writeUInt32BE(width,4);ctl.writeUInt32BE(height,8);ctl.writeUInt16BE(delays[i],20);ctl.writeUInt16BE(1000,22);png.push(chunk('fcTL',ctl));
 for(const item of frames[i].filter(c=>c.type==='IDAT')){if(i===0)png.push(chunk('IDAT',item.data));else{const prefix=Buffer.alloc(4);prefix.writeUInt32BE(sequence++);png.push(chunk('fdAT',Buffer.concat([prefix,item.data])));}}
}
png.push(chunk('IEND',Buffer.alloc(0)));fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,Buffer.concat(png));
// Compatibility GIF uses a fixed palette and bounded 9-bit LZW segments.
function rgb(frame){const bpp=header[9]===6?4:3;if(header[8]!==8||![2,6].includes(header[9])||header[12]!==0)throw Error('Unsupported PNG format');const data=zlib.inflateSync(Buffer.concat(frame.filter(c=>c.type==='IDAT').map(c=>c.data))),stride=width*bpp,pixels=Buffer.alloc(stride*height);let at=0;
 const paeth=(a,b,c)=>{const p=a+b-c,pa=Math.abs(p-a),pb=Math.abs(p-b),pc=Math.abs(p-c);return pa<=pb&&pa<=pc?a:pb<=pc?b:c;};
 for(let y=0;y<height;y++){const filter=data[at++];for(let x=0;x<stride;x++){const a=x>=bpp?pixels[y*stride+x-bpp]:0,b=y?pixels[(y-1)*stride+x]:0,c=y&&x>=bpp?pixels[(y-1)*stride+x-bpp]:0;pixels[y*stride+x]=(data[at++]+(filter===0?0:filter===1?a:filter===2?b:filter===3?Math.floor((a+b)/2):paeth(a,b,c)))&255;}}return {pixels,bpp};}
const gw=Math.min(480,width),gh=Math.round(height*gw/width),gifHeader=Buffer.alloc(13);gifHeader.write('GIF89a');gifHeader.writeUInt16LE(gw,6);gifHeader.writeUInt16LE(gh,8);gifHeader[10]=0xf7;
const palette=Buffer.alloc(768);for(let i=0;i<256;i++){palette[i*3]=Math.round((i>>5)*255/7);palette[i*3+1]=Math.round(((i>>2)&7)*255/7);palette[i*3+2]=(i&3)*85;}
const gif=[gifHeader,palette,Buffer.from([33,255,11]),Buffer.from('NETSCAPE2.0'),Buffer.from([3,1,0,0,0])];
for(let f=0;f<frames.length;f+=2){const {pixels,bpp}=rgb(frames[f]),codes=[];let count=0;codes.push(256);for(let y=0;y<gh;y++)for(let x=0;x<gw;x++){if(count===200){codes.push(256);count=0;}const p=(Math.min(height-1,Math.floor(y*height/gh))*width+Math.min(width-1,Math.floor(x*width/gw)))*bpp;codes.push((pixels[p]&224)|((pixels[p+1]&224)>>3)|(pixels[p+2]>>6));count++;}codes.push(257);const packed=Buffer.alloc(Math.ceil(codes.length*9/8));let bit=0;for(const code of codes){for(let k=0;k<9;k++,bit++)if(code&(1<<k))packed[bit>>3]|=1<<(bit&7);}const gce=Buffer.from([33,249,4,0,0,0,0,0]);gce.writeUInt16LE(Math.max(2,Math.round((delays[f]+(delays[f+1]??0))/10)),4);const desc=Buffer.alloc(10);desc[0]=44;desc.writeUInt16LE(gw,5);desc.writeUInt16LE(gh,7);gif.push(gce,desc,Buffer.from([8]));for(let n=0;n<packed.length;n+=255){const part=packed.subarray(n,n+255);gif.push(Buffer.from([part.length]),part);}gif.push(Buffer.from([0]));}
gif.push(Buffer.from([59]));const gifPath=output.replace(/\.png$/i,'.gif');fs.writeFileSync(gifPath,Buffer.concat(gif));process.stdout.write(JSON.stringify({frames:frames.length,width,height,apng:output,gif:gifPath,apngBytes:fs.statSync(output).size,gifBytes:fs.statSync(gifPath).size}));
