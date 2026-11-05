// ============================================================================
//  BpMonitor — персональный контроль артериального давления (один файл)
//  WinForms-обёртка: трей, автозапуск, popup, mp3-будильник, таймеры
//  HTTP-сервер (System.Net.HttpListener) + компактный Web UI (index.html)
//  Погода: open-meteo (без ключа) + GFZ kp.gfz.de (Kp хранится в десятых: 33 = 3.3)
//  HomeAssistant: опциональный REST-датчики (pressure/temp/humidity/kp)
//  NN: персональная сеть 12 -> H(tanh) -> 2 (сис/диаст), обучается online
//  Хранилище: data/data.bin — бинарный append-журнал (типизированные поля, пул строк);
//    data/nn.bin — модель; config.json. Миграция со старых форматов автоматическая
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using MiniExcelLibs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NAudio.Wave;
namespace BpMonitor{
// ============================== МОДЕЛИ ==================================
public class AlarmDef{
	public string Time="08:00";
	public string Label="";
	public bool Enabled=true;
	public string Mode="daily";// daily | days (по дням недели) | everyN (каждые N дней)
	public List<int> Days=new List<int>();// ISO: 1=пн..7=вс, используется при Mode=days
	public int EveryN=1;// используется при Mode=everyN
	public string Anchor="";// дата старта цикла everyN (yyyyMMdd); пусто -> фиксируется при первом срабатывании
}
public class MedKt{
	public double HalfH=8;// период полувыведения, ч
	public double DurH=12;// длительность действия, ч
}
public class HaCfg{
	public string Url="";
	public string Token="";
	public Dictionary<string,string> Sensors=new Dictionary<string,string>
		{{"pressure",""},{"temp",""},{"humidity",""},{"kp",""}};
}
public class NnCfg{
	public double Lr=0.05;
	public int Hidden=12;
	public int Hidden2=16;// второй скрытый слой
	public int Epochs=30;
}
public class Config{
	public int Port=8090;
	public double Lat=55.75,Lon=37.62;
	public int PollMin=15;// период опроса погоды, мин
	public string AlarmFile="alarm.mp3";// mp3 рядом с exe
	public int AlarmAheadMin=5;// напоминание в трее до будильника
	public bool Autostart=true;
	public int NormSys=135,NormDia=85;// "норма": прогноз выше -> popup
	public int PopupCooldownMin=120;// пауза между popup
	public HaCfg Ha=new HaCfg();
	public NnCfg Nn=new NnCfg();
	public List<AlarmDef> Alarms=new List<AlarmDef>{new AlarmDef{Time="08:00",Label="утро"},new AlarmDef{Time="20:00",Label="вечер"}};
	public List<string> Meds=new List<string>();
	public Dictionary<string,MedKt> MedKt=new Dictionary<string,MedKt>();// PK-переопределения по названию (реестр нечувствителен к регистру)
	public List<string> Noises=new List<string>{
		"сосед с перфоратором","гонки стритрейсеров под окнами","пьяная вечеринка с танцами над головой",
		"громкая музыка у соседей","стройка / ремонт","лающая собака","салют / громкие взрывы","самолёт низко над домом","тихо / ничего"
	};
}
public class Measurement{
	public string Id;
	public DateTime T;
	public int Sys,Dia,Pulse;
	public string Note="";
	// погода на момент измерения (снимок)
	public float? PressMm,TempC,Hum;
	public int? Kp;
	public string Wx="";
	public List<string> Meds=new List<string>();
	// контекст (снимок на момент записи; для NN используется живой джойн)
	public float? EmotionV;
	public string EmotionLabel="";
	public List<string> Noise=new List<string>();
	public float? SleepH;
	public int? SleepQ;
}
public class EmotionEntry{
	public string Id;
	public DateTime T;
	public int Val;
	public string Note="";
}
public class NoiseEntry{
	public string Id;
	public DateTime T;
	public string Type;
	public string Note="";
}
public class SleepEntry{
	public string Id;
	public DateTime T;
	public float Hours;
	public int Quality;
	public string Note="";
}
public class LossPt{
	public string T;
	public double L;
}
public class NnModel{
	public double[,] W1;
	public double[,] W1b;// второй скрытый слой (h2 x h1)
	public double[] B1b;
	public double[] B1;
	public double[,] W2;
	public double[] B2;
	public List<LossPt> Loss=new List<LossPt>();
}
public class DataStore{
	public List<Measurement> M=new List<Measurement>();
	public List<EmotionEntry> E=new List<EmotionEntry>();
	public List<NoiseEntry> N=new List<NoiseEntry>();
	public List<SleepEntry> S=new List<SleepEntry>();
	public NnModel Nn;
	public DateTime? LastPopup;
	public static string EmotionLabel(int v){
		if(v<=-10) return"глубокая горесть (смерть близкого)";
		if(v<=-9) return"отчаяние";
		if(v<=-8) return"сильный страх";
		if(v<=-7) return"гнев";
		if(v<=-6) return"тревога / напряжение";
		if(v<=-5) return"раздражение";
		if(v<=-4) return"досада, суета";
		if(v<=-3) return"лёгкая усталость";
		if(v<=-2) return"небольшая грусть";
		if(v<=-1) return"спокойствие с лёгким негативом";
		if(v==0) return"нейтрально";
		if(v<=1) return"лёгкое удовлетворение";
		if(v<=2) return"покой";
		if(v<=3) return"лёгкая радость";
		if(v<=4) return"хорошее настроение";
		if(v<=5) return"радость";
		if(v<=6) return"воодушевление";
		if(v<=7) return"эйфория";
		if(v<=8) return"восторг (важное событие)";
		return v<=9?"экстремальное счастье":"пиковая радость (рождение ребёнка)";
	}
}

// ============================== ПРИЛОЖЕНИЕ ==============================
static class App{
	private static string ExeDir,CfgPath,DataPath,DataBinPath,IndexPath;
	public static string ExeDirPath=>ExeDir;
	public static Config Cfg=new Config();
	public static DataStore D=new DataStore();
	static HttpClient Http=new HttpClient{Timeout=TimeSpan.FromSeconds(15)};
	static readonly object Io=new object();
	public static readonly object Sync=new object();// вся мутация D/Cfg — только под этим замком
	public const string TokenMask="********";
	static double Clamp(double v,double lo,double hi){ return v<lo?lo:(v>hi?hi:v); }
	public static readonly string[] FeatBase={
		"час sin","час cos","день sin","день cos","давл. мм","температура","влажность","Kp",
		"эмоция","шум(кол-во)","сон ч","лекарств(кол-во)"
	};
	// полные имена фич: базовые + по 2 на каждое лекарство (активность/окно)
	public static string[] GetFeatNames(){
		List<string> l=new List<string>(FeatBase);
		foreach(string med in Cfg.Meds){l.Add(med+": активность");l.Add(med+": окно");}
		return l.ToArray();
	}
	// размерность входа НН зависит от списка лекарств
	public static int InDim(){ return FeatBase.Length+2*Cfg.Meds.Count; }
	public class Wx{
		public float? PressMm,TempC,Hum;
		public int? Kp;
		public string Text="";
		public DateTime? Updated;
	}
	public static Wx Weather=new Wx();
	static bool _weatherBusy;
	static readonly JsonSerializerSettings JSONSettings=new JsonSerializerSettings{ObjectCreationHandling=ObjectCreationHandling.Replace};
	// ----------------------------- пути/загрузка -----------------------------
	public static void InitPaths(){
		ExeDir=AppDomain.CurrentDomain.BaseDirectory;
		CfgPath=Path.Combine(ExeDir,"config.json");
		DataPath=Path.Combine(ExeDir,"data","data.json");
		DataBinPath=Path.Combine(ExeDir,"data","data.bin");
		IndexPath=Path.Combine(ExeDir,"index.html");
	}
	public static void Load(){
		try{
			if(File.Exists(CfgPath))
				Cfg=JsonConvert.DeserializeObject<Config>(File.ReadAllText(CfgPath,Encoding.UTF8),JSONSettings)??new Config();
				Cfg.Meds=Cfg.Meds.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();// самолечение: дедуп списка лекарств без учёта регистра
				Cfg.Noises=Cfg.Noises.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();// дедуп шумов (старые конфиги содержали 4x дубли до фикса Replace)
				if(Cfg.MedKt!=null){ Dictionary<string,MedKt> mk=new Dictionary<string,MedKt>(); foreach(KeyValuePair<string,MedKt> kv in Cfg.MedKt) if(!string.IsNullOrWhiteSpace(kv.Key)) mk[NormMed(kv.Key)]=kv.Value; Cfg.MedKt=mk; }
		} catch{ Quarantine(CfgPath); Cfg=new Config(); }
		bool migrated=false,nnLoaded=false;
		string dataFile=null;
		try{
			if(File.Exists(DataBinPath)&&IsBpmn(DataBinPath)){// v2: бинарный журнал
				LoadBin();
				nnLoaded=LoadNn();
			}else if(File.Exists(DataBinPath)){// v1: gzip-JSON — мигрируем в бинарный журнал
				using(FileStream fs=File.OpenRead(DataBinPath))
				using(GZipStream gz=new GZipStream(fs,CompressionMode.Decompress))
				using(StreamReader sr=new StreamReader(gz,Encoding.UTF8))
					D=JsonConvert.DeserializeObject<DataStore>(sr.ReadToEnd())??new DataStore();
				migrated=true;
			}else if(File.Exists(DataPath)){// v0: простой JSON
				D=JsonConvert.DeserializeObject<DataStore>(File.ReadAllText(DataPath,Encoding.UTF8))??new DataStore();
				migrated=true; dataFile=DataPath;
			}
		} catch{ Quarantine(dataFile??DataBinPath); D=new DataStore(); }// битый файл спасён от перезаписи
		if(Cfg==null) Cfg=new Config();
		if(D==null) D=new DataStore();
		bool kpChanged=MigrateKp();
		EnsureNnDim();		if(migrated||kpChanged){
			Save();// полный снимок: переезд на бинарный формат
			if(dataFile!=null) try{ File.Move(dataFile,dataFile+".old-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch{}
		}
		else if(!nnLoaded) SaveNn();
		else if(_logRecords>2*(D.M.Count+D.E.Count+D.N.Count+D.S.Count)+64)
			Save();// компакция: накопилось слишком много tombstone/обновлений
	}
	static void Quarantine(string path){
		try{ File.Move(path,path+".bad-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch{}
	}
	// Kp во всех источниках — десятые доли (33 = 3.3). Старые записи: GFZ писал Kp*100, HA — 0..9
	static bool MigrateKp(){
		bool changed=false;
		foreach(Measurement m in D.M){
			if(m.Kp==null) continue;
			int v=m.Kp.Value,nv=v;
			if(v>90) nv=v/10;
			else if(v>=0&&v<=9) nv=v*10;
			if(nv!=v){ m.Kp=nv; changed=true; }
		}
		return changed;
	}
	public static void Save(){// полный снимок: миграция/компакция/выход
		lock(Io){
			try{
				File.WriteAllText(CfgPath,JsonConvert.SerializeObject(Cfg,Formatting.Indented));
				Directory.CreateDirectory(Path.GetDirectoryName(DataBinPath)??"");
				string tmp=DataBinPath+".tmp";
				_pool.Clear(); _poolNext=0;
				using(BinaryWriter w=new BinaryWriter(File.Create(tmp))){
					w.Write(BinMagic); w.Write(BinVer);
					Func<string,int> pool=PoolWriter(w);
					foreach(Measurement m in D.M.OrderBy(x=>x.T)) WriteRec(w,RecMeas,MeasPayload(m,pool));
					foreach(EmotionEntry e in D.E.OrderBy(x=>x.T)) WriteRec(w,RecEmo,EmoPayload(e,pool));
					foreach(NoiseEntry n2 in D.N.OrderBy(x=>x.T)) WriteRec(w,RecNoi,NoiPayload(n2,pool));
					foreach(SleepEntry s2 in D.S.OrderBy(x=>x.T)) WriteRec(w,RecSleep,SleepPayload(s2,pool));
					if(D.LastPopup!=null) WriteRec(w,RecPopup,RecPayload(w2=>w2.Write(D.LastPopup.Value.Ticks)));
				}
				if(File.Exists(DataBinPath)) File.Delete(DataBinPath);
				File.Move(tmp,DataBinPath);
			} catch{
				/* В/В не критичен для живости UI */
			}
		}
		SaveNn();
	}
	public static void SaveCfg(){
		lock(Io){ try{ File.WriteAllText(CfgPath,JsonConvert.SerializeObject(Cfg,Formatting.Indented)); } catch{} }
	}
	public static void SaveNn(){// модель маленькая (~1-8 КБ) — переписываем целиком
		lock(Io){
			try{
				Directory.CreateDirectory(Path.GetDirectoryName(NnPath)??"");
				string tmp=NnPath+".tmp";
				NnModel n=D.Nn;
				using(BinaryWriter w=new BinaryWriter(File.Create(tmp))){
					w.Write(BinMagic); w.Write(BinVer); w.Write((byte)3);// формат весов v3: 2 скрытых слоя
					int h=n.W1.GetLength(0),f=n.W1.GetLength(1),h2=n.W1b.GetLength(0);
					w.Write(h); w.Write(f); w.Write(h2);
					for(int i=0;i<h;i++) for(int j=0;j<f;j++) w.Write(n.W1[i,j]);
					for(int i=0;i<h;i++) w.Write(n.B1[i]);
					for(int i=0;i<h2;i++) for(int j=0;j<h;j++) w.Write(n.W1b[i,j]);
					for(int i=0;i<h2;i++) w.Write(n.B1b[i]);
					for(int o=0;o<2;o++) for(int i=0;i<h2;i++) w.Write(n.W2[o,i]);
					for(int o=0;o<2;o++) w.Write(n.B2[o]);
					w.Write(n.Loss.Count);
					foreach(LossPt lp in n.Loss){
						DateTime t;
						if(!DateTime.TryParseExact(lp.T,"MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out t)) t=DateTime.MinValue;
						w.Write(t.Ticks); w.Write(lp.L);
					}
				}
				if(File.Exists(NnPath)) File.Delete(NnPath);
				File.Move(tmp,NnPath);
			} catch{}
		}
	}
	// ----------------------------- бинарное хранилище v2: append-журнал -----------------------------
	// data.bin = заголовок + поток записей [byte тип][int длина][payload]. Новая запись — дописка
	// нескольких десятков байт, файл целиком не переписывается. Строки — через пул (ushort id),
	// числа — типизированно (битовые флаги присутствия/ushort/float/sbyte). Полная перезапись
	// data.bin только при компакции/миграции. nn.bin — модель, маленькая, переписывается всегда.
	const uint BinMagic=0x4E4D5042;// "BPMN"
	const byte BinVer=2;
	const byte RecStr=1,RecMeas=0x10,RecMeasDel=0x11,RecEmo=0x20,RecEmoDel=0x21,RecNoi=0x30,RecNoiDel=0x31,RecSleep=0x40,RecSleepDel=0x41,RecPopup=0x50;
	static Dictionary<string,int> _pool=new Dictionary<string,int>();
	static int _poolNext;
	static int _logRecords;
	static string NnPath{ get{ return Path.Combine(ExeDir,"data","nn.bin"); } }
	static byte[] RecPayload(Action<BinaryWriter> fill){
		using(MemoryStream ms=new MemoryStream()){
			using(BinaryWriter pw=new BinaryWriter(ms)) fill(pw);
			return ms.ToArray();
		}
	}
	static void WriteRec(BinaryWriter w,byte type,byte[] payload){ w.Write(type); w.Write(payload.Length); w.Write(payload); }
	static Func<string,int> PoolWriter(BinaryWriter w){// новая строка -> Str-запись в этот же writer
		return str=>{
			str=str??"";
			int id;
			if(_pool.TryGetValue(str,out id)) return id;
			id=_poolNext++; _pool[str]=id;
			WriteRec(w,RecStr,RecPayload(pw=>{ pw.Write((ushort)id); pw.Write(str); }));
			return id;
		};
	}
	static byte[] MeasPayload(Measurement m,Func<string,int> pool){
		return RecPayload(w=>{
			Guid g; Guid.TryParse(m.Id,out g);
			w.Write(g.ToByteArray());// 16
			w.Write(m.T.Ticks);// 8
			w.Write((ushort)m.Sys); w.Write((ushort)m.Dia); w.Write((ushort)m.Pulse);// 6
			byte fl=0;// биты присутствия опциональных полей
			if(m.PressMm!=null)fl|=1; if(m.TempC!=null)fl|=2; if(m.Hum!=null)fl|=4; if(m.Kp!=null)fl|=8;
			if(m.EmotionV!=null)fl|=16; if(m.SleepH!=null)fl|=32; if(m.SleepQ!=null)fl|=64;
			w.Write(fl);
			if(m.PressMm!=null)w.Write(m.PressMm.Value);
			if(m.TempC!=null)w.Write(m.TempC.Value);
			if(m.Hum!=null)w.Write(m.Hum.Value);
			if(m.Kp!=null)w.Write((short)m.Kp.Value);
			if(m.EmotionV!=null)w.Write((sbyte)m.EmotionV.Value);
			if(m.SleepH!=null)w.Write(m.SleepH.Value);
			if(m.SleepQ!=null)w.Write((byte)m.SleepQ.Value);
			w.Write((ushort)(string.IsNullOrEmpty(m.Wx)?0xFFFF:pool(m.Wx)));
			w.Write((byte)Math.Min(255,m.Meds.Count));
			foreach(string s3 in m.Meds.Take(255)) w.Write((ushort)pool(s3));
			w.Write((byte)Math.Min(255,m.Noise.Count));
			foreach(string s3 in m.Noise.Take(255)) w.Write((ushort)pool(s3));
			byte[] nb=Encoding.UTF8.GetBytes(m.Note??"");
			w.Write((ushort)Math.Min(65535,nb.Length)); w.Write(nb,0,Math.Min(65535,nb.Length));
		});
	}
	static byte[] EmoPayload(EmotionEntry e,Func<string,int> pool){
		return RecPayload(w=>{
			Guid g; Guid.TryParse(e.Id,out g);
			w.Write(g.ToByteArray()); w.Write(e.T.Ticks); w.Write((sbyte)e.Val);
			byte[] nb=Encoding.UTF8.GetBytes(e.Note??""); w.Write((ushort)nb.Length); w.Write(nb);
		});
	}
	static byte[] NoiPayload(NoiseEntry n2,Func<string,int> pool){
		return RecPayload(w=>{
			Guid g; Guid.TryParse(n2.Id,out g);
			w.Write(g.ToByteArray()); w.Write(n2.T.Ticks);
			w.Write((ushort)pool(n2.Type??""));
			byte[] nb=Encoding.UTF8.GetBytes(n2.Note??""); w.Write((ushort)nb.Length); w.Write(nb);
		});
	}
	static byte[] SleepPayload(SleepEntry s2,Func<string,int> pool){
		return RecPayload(w=>{
			Guid g; Guid.TryParse(s2.Id,out g);
			w.Write(g.ToByteArray()); w.Write(s2.T.Ticks); w.Write(s2.Hours); w.Write((byte)s2.Quality);
			byte[] nb=Encoding.UTF8.GetBytes(s2.Note??""); w.Write((ushort)nb.Length); w.Write(nb);
		});
	}
	static BinaryWriter OpenAppend(){
		Directory.CreateDirectory(Path.GetDirectoryName(DataBinPath)??"");
		bool fresh=!File.Exists(DataBinPath)||new FileInfo(DataBinPath).Length==0;
		BinaryWriter w=new BinaryWriter(File.Open(DataBinPath,FileMode.Append));
		if(fresh){ w.Write(BinMagic); w.Write(BinVer); }// журнал создан дописыванием — заголовок на месте
		return w;
	}
	static void AppendRec(byte type,byte[] payload){
		lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,type,payload); } catch{} }
	}
	static void AppendMeasurement(Measurement m){ lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,RecMeas,MeasPayload(m,PoolWriter(w))); } catch{} } }
	static void AppendEmotion(EmotionEntry e){ lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,RecEmo,EmoPayload(e,PoolWriter(w))); } catch{} } }
	static void AppendNoise(NoiseEntry n2){ lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,RecNoi,NoiPayload(n2,PoolWriter(w))); } catch{} } }
	static void AppendSleep(SleepEntry s2){ lock(Io){ try{ using(BinaryWriter w=OpenAppend()) WriteRec(w,RecSleep,SleepPayload(s2,PoolWriter(w))); } catch{} } }
	static void AppendGuidRec(byte type,string id){ Guid g; if(Guid.TryParse(id,out g)) AppendRec(type,RecPayload(w=>w.Write(g.ToByteArray()))); }
	static void AppendPopup(){ if(D.LastPopup!=null) AppendRec(RecPopup,RecPayload(w=>w.Write(D.LastPopup.Value.Ticks))); }
	static bool SameId(string id,Guid g){ Guid g2; return Guid.TryParse(id,out g2)&&g2==g; }
	static string GetPool(Dictionary<int,string> pool,int id){ string s3; return pool.TryGetValue(id,out s3)?s3:""; }
	static bool IsBpmn(string path){
		try{ using(BinaryReader r=new BinaryReader(File.OpenRead(path))) return r.ReadUInt32()==BinMagic; } catch{ return false; }
	}
	static void LoadBin(){
		_pool.Clear(); _poolNext=0; _logRecords=0;
		Dictionary<int,string> pool=new Dictionary<int,string>();
		byte[] all=File.ReadAllBytes(DataBinPath);
		using(MemoryStream ms=new MemoryStream(all))
		using(BinaryReader r=new BinaryReader(ms)){
			if(r.ReadUInt32()!=BinMagic) throw new Exception("bad data.bin magic");
			if(r.ReadByte()!=BinVer) throw new Exception("bad data.bin version");
			try{
				while(r.BaseStream.Position<r.BaseStream.Length){
					byte type=r.ReadByte(); int len=r.ReadInt32();
					if(len<0||r.BaseStream.Position+len>r.BaseStream.Length) break;// оборванный хвост после краша
					byte[] pl=r.ReadBytes(len); _logRecords++;
					using(BinaryReader p=new BinaryReader(new MemoryStream(pl))){
						if(type==RecStr){ int id=p.ReadUInt16(); string sv=p.ReadString(); pool[id]=sv; if(id>=_poolNext) _poolNext=id+1; }
						else if(type==RecMeas){
							Guid g=new Guid(p.ReadBytes(16)); DateTime t=new DateTime(p.ReadInt64());
							int sys=p.ReadUInt16(),dia=p.ReadUInt16(),pulse=p.ReadUInt16();
							byte fl=p.ReadByte();
							float? press=(fl&1)!=0?(float?)p.ReadSingle():null;
							float? temp=(fl&2)!=0?(float?)p.ReadSingle():null;
							float? hum=(fl&4)!=0?(float?)p.ReadSingle():null;
							int? kp=(fl&8)!=0?(int?)p.ReadInt16():null;
							float? emo=(fl&16)!=0?(float?)p.ReadSByte():null;
							float? sleepH=(fl&32)!=0?(float?)p.ReadSingle():null;
							int? sleepQ=(fl&64)!=0?(int?)p.ReadByte():null;
							int wxId=p.ReadUInt16();
							int mc=p.ReadByte(); List<string> meds=new List<string>();
							for(int i2=0;i2<mc;i2++) meds.Add(GetPool(pool,p.ReadUInt16()));
							int nc=p.ReadByte(); List<string> noise=new List<string>();
							for(int i2=0;i2<nc;i2++) noise.Add(GetPool(pool,p.ReadUInt16()));
							string note=Encoding.UTF8.GetString(p.ReadBytes(p.ReadUInt16()));
							D.M.RemoveAll(x=>SameId(x.Id,g));
							D.M.Add(new Measurement{
								Id=g.ToString("N"),T=t,Sys=sys,Dia=dia,Pulse=pulse,
								PressMm=press,TempC=temp,Hum=hum,Kp=kp,SleepH=sleepH,SleepQ=sleepQ,
								EmotionV=emo,EmotionLabel=emo!=null?DataStore.EmotionLabel((int)emo.Value):"",
								Wx=wxId==0xFFFF?"":GetPool(pool,wxId),Meds=meds,Noise=noise,Note=note});
						}
						else if(type==RecMeasDel){ Guid g=new Guid(p.ReadBytes(16)); D.M.RemoveAll(x=>SameId(x.Id,g)); }
						else if(type==RecEmo){
							Guid g=new Guid(p.ReadBytes(16)); DateTime t=new DateTime(p.ReadInt64());
							int val=p.ReadSByte(); string note=Encoding.UTF8.GetString(p.ReadBytes(p.ReadUInt16()));
							D.E.RemoveAll(x=>SameId(x.Id,g));
							D.E.Add(new EmotionEntry{Id=g.ToString("N"),T=t,Val=val,Note=note});
						}
						else if(type==RecEmoDel){ Guid g=new Guid(p.ReadBytes(16)); D.E.RemoveAll(x=>SameId(x.Id,g)); }
						else if(type==RecNoi){
							Guid g=new Guid(p.ReadBytes(16)); DateTime t=new DateTime(p.ReadInt64());
							string tp=GetPool(pool,p.ReadUInt16()); string note=Encoding.UTF8.GetString(p.ReadBytes(p.ReadUInt16()));
							D.N.RemoveAll(x=>SameId(x.Id,g));
							D.N.Add(new NoiseEntry{Id=g.ToString("N"),T=t,Type=tp,Note=note});
						}
						else if(type==RecNoiDel){ Guid g=new Guid(p.ReadBytes(16)); D.N.RemoveAll(x=>SameId(x.Id,g)); }
						else if(type==RecSleep){
							Guid g=new Guid(p.ReadBytes(16)); DateTime t=new DateTime(p.ReadInt64());
							float hours=p.ReadSingle(); int q=p.ReadByte(); string note=Encoding.UTF8.GetString(p.ReadBytes(p.ReadUInt16()));
							D.S.RemoveAll(x=>SameId(x.Id,g));
							D.S.Add(new SleepEntry{Id=g.ToString("N"),T=t,Hours=hours,Quality=q,Note=note});
						}
						else if(type==RecSleepDel){ Guid g=new Guid(p.ReadBytes(16)); D.S.RemoveAll(x=>SameId(x.Id,g)); }
						else if(type==RecPopup) D.LastPopup=new DateTime(p.ReadInt64());
						// неизвестный тип — пропущен (задел на будущее)
					}
				}
			} catch(Exception){// битая/оборванная запись — останавливаемся, что прочитано, то прочитано
				if(_logRecords==0) throw;// файл совсем битой — пусть уходит в карантин
			}
		}
		foreach(KeyValuePair<int,string> kv in pool) _pool[kv.Value]=kv.Key;
	}
	static bool LoadNn(){
		if(!File.Exists(NnPath)) return false;
		try{
			using(BinaryReader r=new BinaryReader(File.OpenRead(NnPath))){
				if(r.ReadUInt32()!=BinMagic||r.ReadByte()!=BinVer) return false;
				if(r.ReadByte()!=3) return false;// старый однослойный формат -> пересоздать
				int h=r.ReadInt32(),f=r.ReadInt32(),h2=r.ReadInt32();
				NnModel n=new NnModel{ W1=new double[h,f],B1=new double[h],W1b=new double[h2,h],B1b=new double[h2],W2=new double[2,h2],B2=new double[2] };
				for(int i=0;i<h;i++) for(int j=0;j<f;j++) n.W1[i,j]=r.ReadDouble();
				for(int i=0;i<h;i++) n.B1[i]=r.ReadDouble();
				for(int i=0;i<h2;i++) for(int j=0;j<h;j++) n.W1b[i,j]=r.ReadDouble();
				for(int i=0;i<h2;i++) n.B1b[i]=r.ReadDouble();
				for(int o=0;o<2;o++) for(int i=0;i<h2;i++) n.W2[o,i]=r.ReadDouble();
				for(int o=0;o<2;o++) n.B2[o]=r.ReadDouble();
				int lc=r.ReadInt32();
				for(int i=0;i<lc;i++){ long ticks=r.ReadInt64(); double l=r.ReadDouble(); n.Loss.Add(new LossPt{ T=new DateTime(ticks).ToString("MM-dd HH:mm"),L=l }); }
				D.Nn=n;
				return true;
			}
		} catch{ return false; }
	}

	// ----------------------------- НН: инициализация -----------------------------
	public static NnModel NewNn(int hidden,int hidden2,int f){
		Random rnd=new Random(42);
		double[,] w1=new double[hidden,f];
		double[] b1=new double[hidden];
		double[,] w1b=new double[hidden2,hidden];
		double[] b1b=new double[hidden2];
		double[,] w2=new double[2,hidden2];
		double[] b2=new double[2];
		for(int i=0;i<hidden;i++)
		for(int j=0;j<f;j++)
			w1[i,j]=(rnd.NextDouble()*2-1)*0.4;
		for(int i=0;i<hidden2;i++)
		for(int j=0;j<hidden;j++)
			w1b[i,j]=(rnd.NextDouble()*2-1)*0.4;
		for(int i=0;i<2;i++)
		for(int j=0;j<hidden2;j++)
			w2[i,j]=(rnd.NextDouble()*2-1)*0.4;
		return new NnModel{W1=w1,B1=b1,W1b=w1b,B1b=b1b,W2=w2,B2=b2};
	}

	// ----------------------------- НН: фичи -----------------------------
	// контекст для НН: запись засчитывается, если сделана не раньше окна и не позже 10 мин ПОСЛЕ измерения
	// (обычный workflow: померил — потом сразу записал эмоцию/шум/сон). Нет записи или нулевое значение -> нейтраль
	static float EmotionAt(DateTime t){
		EmotionEntry e=D.E.Where(x=>x.T<=t.AddMinutes(10)&&t-x.T<=TimeSpan.FromHours(12)).OrderByDescending(x=>x.T).FirstOrDefault();
		return e?.Val??0f;// шкала -10..+10, 0 = нейтрально
	}
	static int NoiseAt(DateTime t){
		int ns=D.N.Where(x=>x.T<=t.AddMinutes(10)&&t-x.T<=TimeSpan.FromHours(3)).Select(x=>x.Type).Distinct().Count();
		return ns;// нет записей = 0 = тихо, это и есть нейтраль
	}
	static float SleepAt(DateTime t){
		SleepEntry s=D.S.Where(x=>x.T<=t.AddMinutes(10)&&t-x.T<=TimeSpan.FromHours(36)).OrderByDescending(x=>x.T).FirstOrDefault();
		return s!=null&&s.Hours>0?s.Hours:7.5f;// нет/ноль -> нейтральные 7.5 ч
	}
	// ----------------------------- фармакокинетика лекарств -----------------------------
	// T½ / длительность действия, часы. Источники: StatPearls NCBI NBK554579, DailyMed,
	// PMC8109523 (АКК), SmPC бисопролола, Br J Clin Pharmacol (метотрексат низкие дозы).
	// Значения по умолчанию для неизвестных: 8/12 ч — правятся руками в UI.
	static readonly Dictionary<string,double[]> MedPkDb=new Dictionary<string,double[]>{
		{"каптоприл",new[]{2.0,8.0}},{"эналаприл",new[]{11.0,24.0}},{"лизиноприл",new[]{12.0,24.0}},
		{"рамиприл",new[]{15.0,24.0}},{"периндоприл",new[]{24.0,24.0}},
		{"амлодипин",new[]{40.0,36.0}},{"нифедипин",new[]{2.0,8.0}},{"фелодипин",new[]{14.0,24.0}},
		{"дилтиазем",new[]{4.0,12.0}},{"верапамил",new[]{7.0,12.0}},
		{"лозартан",new[]{6.0,24.0}},{"валсартан",new[]{7.0,24.0}},{"телмисартан",new[]{24.0,24.0}},
		{"кандесартан",new[]{9.0,24.0}},{"ирбесартан",new[]{13.0,24.0}},{"олмесартан",new[]{13.0,24.0}},
		{"бисопролол",new[]{10.0,24.0}},{"метопролол",new[]{4.0,12.0}},{"атенолол",new[]{7.0,24.0}},
		{"небиволол",new[]{2.0,24.0}},{"небилет",new[]{2.0,24.0}},{"каведилол",new[]{8.0,24.0}},
		{"гидрохлоротиазид",new[]{10.0,12.0}},{"хлорталидон",new[]{45.0,60.0}},{"индапамид",new[]{16.0,24.0}},
		{"спиронолактон",new[]{18.0,24.0}},{"верошпирон",new[]{18.0,24.0}},
		{"фуросемид",new[]{2.0,6.0}},{"торасемид",new[]{4.0,8.0}},
		{"моксонидин",new[]{3.0,8.0}},{"физиотенз",new[]{3.0,8.0}},{"клофелин",new[]{14.0,8.0}},
		{"метилдопа",new[]{2.0,12.0}},{"метотрексат",new[]{7.0,24.0}},{"фолиевая",new[]{6.0,24.0}},
		{"ибупрофен",new[]{2.0,6.0}},{"диклофенак",new[]{2.0,6.0}},{"преднизолон",new[]{3.0,24.0}},
		// торговые марки (РФ)
		{"энап",new[]{11.0,24.0}},{"диротон",new[]{15.0,24.0}},{"престариум",new[]{24.0,24.0}},{"нолипрел",new[]{24.0,24.0}},
		{"лозап",new[]{6.0,24.0}},{"вальсакор",new[]{7.0,24.0}},{"микорам",new[]{24.0,24.0}},
		{"эгилок",new[]{4.0,12.0}},{"вазокардин",new[]{4.0,12.0}},{"корвитол",new[]{4.0,12.0}},
		{"капозид",new[]{2.0,8.0}},{"телинстар",new[]{24.0,24.0}},{"ко-диован",new[]{7.0,24.0}},
		{"крестор",new[]{19.0,24.0}},{"розувастатин",new[]{19.0,24.0}},{"аторвастатин",new[]{14.0,24.0}}
	};
	static string NormMed(string s){
		return new string((s??"").ToLowerInvariant().Replace('ё','е').Where(char.IsLetterOrDigit).ToArray());
	}
	// действующая PK: пользовательский override -> справочник -> 8/12 ч
	public static (double half,double dur) MedKtFor(string name){
		string n=NormMed(name);
		if(Cfg.MedKt!=null) foreach(KeyValuePair<string,MedKt> kv in Cfg.MedKt)
			if(kv.Value!=null&&NormMed(kv.Key)==n) return (Math.Max(0.25,kv.Value.HalfH),Math.Max(0.5,kv.Value.DurH));
		foreach(KeyValuePair<string,double[]> kv in MedPkDb)
			if(n==kv.Key||n.Contains(kv.Key)) return (kv.Value[0],kv.Value[1]);
		return (8.0,12.0);
	}
	// времена приёма каждого лекарства: приём фиксируется в измерении, где оно отмечено
	static Dictionary<string,List<DateTime>> MedIntakes(){
		Dictionary<string,List<DateTime>> d=new Dictionary<string,List<DateTime>>();
		foreach(Measurement m in D.M) foreach(string s in m.Meds){
			string k=NormMed(s); if(k=="") continue;
			List<DateTime> l;
			if(!d.TryGetValue(k,out l)){l=new List<DateTime>();d[k]=l;}
			l.Add(m.T);
		}
		return d;
	}
	// размерность входа зависит от списка лекарств; при изменении — переучиваем модель с нуля
	public static void EnsureNnDim(){
		if(D.Nn?.W1!=null&&D.Nn.W1b!=null&&D.Nn.W1.GetLength(1)==InDim()&&D.Nn.W1.GetLength(0)==Cfg.Nn.Hidden&&D.Nn.W1b.GetLength(0)==Cfg.Nn.Hidden2) return;		D.Nn=NewNn(Cfg.Nn.Hidden,Cfg.Nn.Hidden2,InDim());		Train();
		SaveNn();
	}

	public static double[] FeatsFor(Measurement m,Dictionary<string,List<DateTime>> intakes=null){
		if(intakes==null) intakes=MedIntakes();
		double h=m.T.Hour+m.T.Minute/60.0;
		double dow=((int)m.T.DayOfWeek+6)%7;
		double[] x=new double[InDim()];
		x[0]=Math.Sin(2*Math.PI*h/24); x[1]=Math.Cos(2*Math.PI*h/24);
		x[2]=Math.Sin(2*Math.PI*dow/7); x[3]=Math.Cos(2*Math.PI*dow/7);
		x[4]=((double)(m.PressMm??1013f)-1013)/40;
		x[5]=(double)(m.TempC??15f)/15;
		x[6]=(double)(m.Hum??60f)/100;
		x[7]=(double)(m.Kp??33)/90;
		x[8]=EmotionAt(m.T)/10.0;
		x[9]=NoiseAt(m.T)/4.0;
		x[10]=SleepAt(m.T)/10.0;
		x[11]=m.Meds.Count/4.0;
		for(int k=0;k<Cfg.Meds.Count;k++){// PK-фичи: остаточная активность и нахождение в окне действия
			(double half,double dur)=MedKtFor(Cfg.Meds[k]);
			double conc=0,win=0;
			List<DateTime> ts;
			if(intakes.TryGetValue(NormMed(Cfg.Meds[k]),out ts)) foreach(DateTime ti in ts){
				if(ti>m.T) continue;
				double dt=(m.T-ti).TotalHours;
				conc+=Math.Pow(0.5,dt/half);// элиминация 1-го порядка: "остаток дозы"
				if(dt<=dur){double w=1-dt/dur;if(w>win)win=w;}
			}
			x[FeatBase.Length+2*k]=Math.Min(conc,3.0)/3.0;
			x[FeatBase.Length+2*k+1]=win;
		}
		return x;
	}
	static double[] Forward(NnModel n,double[] x){
		int h1=n.W1.GetLength(0),h2=n.W1b.GetLength(0);
		double[] a1=new double[h1];
		for(int i=0;i<h1;i++){
			double s=n.B1[i];
			for(int j=0;j<x.Length;j++) s+=n.W1[i,j]*x[j];
			a1[i]=Math.Tanh(s);
		}
		double[] a2=new double[h2];
		for(int i=0;i<h2;i++){
			double s=n.B1b[i];
			for(int j=0;j<h1;j++) s+=n.W1b[i,j]*a1[j];
			a2[i]=Math.Tanh(s);
		}
		double[] y=new double[2];
		for(int o=0;o<2;o++){
			double s=n.B2[o];
			for(int i=0;i<h2;i++) s+=n.W2[o,i]*a2[i];
			y[o]=s;
		}
		return y;
	}
	public static(double sys,double dia) PredictNow(){
		Measurement m=new Measurement{
			T=DateTime.Now,
			PressMm=Weather.PressMm,TempC=Weather.TempC,Hum=Weather.Hum,Kp=Weather.Kp
		};
		double[] y=Forward(D.Nn,FeatsFor(m));
		double sys=Clamp(120+30*y[0],70,250),dia=Clamp(80+20*y[1],40,150);
		if(dia>=sys) dia=sys-20;
		return(sys,dia);
	}

	// Обучает НН по всем записям (online: вызывается при каждом вводе)
	public static void Train(){
		NnModel n=D.Nn;
		int hidden=n.W1.GetLength(0),f=n.W1.GetLength(1),h2=n.W1b.GetLength(0);		Dictionary<string,List<DateTime>> intakes=MedIntakes();
		List<double[]> xs=new List<double[]>();
		List<double[]> ys=new List<double[]>();
		foreach(Measurement m in D.M.Where(m=>m.Sys>0).OrderByDescending(m=>m.T).Take(2000)){
			xs.Add(FeatsFor(m,intakes));
			ys.Add(new[]{(m.Sys-120.0)/30.0,(m.Dia-80.0)/20.0});
		}
		if(xs.Count<2) return;
		Random rnd=new Random();
		double lr=Cfg.Nn.Lr;
		for(int ep=0;ep<Math.Max(1,Cfg.Nn.Epochs);ep++){
			for(int i=xs.Count-1;i>0;i--){
				int j=rnd.Next(i+1);
				(xs[i],xs[j])=(xs[j],xs[i]);
				(ys[i],ys[j])=(ys[j],ys[i]);
			}
			double sum=0;
			for(int s=0;s<xs.Count;s++){
				double[] x=xs[s];
				double[] y=ys[s];
				double[] a1=new double[hidden];
				double[] d1=new double[hidden];
				for(int i=0;i<hidden;i++){
					double t=n.B1[i];
					for(int k2=0;k2<f;k2++) t+=n.W1[i,k2]*x[k2];
					a1[i]=Math.Tanh(t);
					d1[i]=a1[i]*(1-a1[i]*a1[i]);
				}
				double[] a2=new double[h2];
				double[] d2=new double[h2];
				for(int i=0;i<h2;i++){
					double t=n.B1b[i];
					for(int j2=0;j2<hidden;j2++) t+=n.W1b[i,j2]*a1[j2];
					a2[i]=Math.Tanh(t);
					d2[i]=a2[i]*(1-a2[i]*a2[i]);
				}
				double[] err=new double[2];
				for(int o=0;o<2;o++){
					double pr=n.B2[o];
					for(int i=0;i<h2;i++) pr+=n.W2[o,i]*a2[i];
					err[o]=pr-y[o];
					sum+=err[o]*err[o];
				}
				// backprop по СТАРЫМ весам: слой2 -> слой1
				double[] dh2=new double[h2];
				for(int i=0;i<h2;i++){
					double g3=0;
					for(int o=0;o<2;o++) g3+=n.W2[o,i]*err[o];
					dh2[i]=d2[i]*g3;
				}
				double[] dh1=new double[hidden];
				for(int j2=0;j2<hidden;j2++){
					double g2=0;
					for(int i=0;i<h2;i++) g2+=n.W1b[i,j2]*dh2[i];
					dh1[j2]=d1[j2]*g2;
				}
				for(int o=0;o<2;o++)
				for(int i=0;i<h2;i++)
					n.W2[o,i]-=err[o]*a2[i]*lr;
				for(int o=0;o<2;o++) n.B2[o]-=err[o]*lr;
				for(int i=0;i<h2;i++)
				for(int j2=0;j2<hidden;j2++)
					n.W1b[i,j2]-=dh2[i]*a1[j2]*lr;
				for(int i=0;i<h2;i++) n.B1b[i]-=dh2[i]*lr;
				for(int i=0;i<hidden;i++){
					double g=dh1[i]*lr;
					for(int k2=0;k2<f;k2++) n.W1[i,k2]-=g*x[k2];
					n.B1[i]-=g;
				}
			}
			if(ep!=Math.Max(1,Cfg.Nn.Epochs)-1) continue;
			n.Loss.Add(new LossPt{T=DateTime.Now.ToString("MM-dd HH:mm"),L=Math.Round(sum/(xs.Count*2.0),4)});
			if(n.Loss.Count>500) n.Loss.RemoveRange(0,n.Loss.Count-500);
		}
	}

	// ----------------------------- НН: генетическое обучение -----------------------------
	static double[] Pack(NnModel n){
		List<double> v=new List<double>();
		foreach(double d in n.W1) v.Add(d); foreach(double d in n.B1) v.Add(d);
		foreach(double d in n.W1b) v.Add(d); foreach(double d in n.B1b) v.Add(d);
		foreach(double d in n.W2) v.Add(d); foreach(double d in n.B2) v.Add(d);
		return v.ToArray();
	}
	static void Unpack(NnModel n,double[] v){
		int q=0;
		for(int i=0;i<n.W1.GetLength(0);i++) for(int j=0;j<n.W1.GetLength(1);j++) n.W1[i,j]=v[q++];
		for(int i=0;i<n.B1.Length;i++) n.B1[i]=v[q++];
		for(int i=0;i<n.W1b.GetLength(0);i++) for(int j=0;j<n.W1b.GetLength(1);j++) n.W1b[i,j]=v[q++];
		for(int i=0;i<n.B1b.Length;i++) n.B1b[i]=v[q++];
		for(int i=0;i<n.W2.GetLength(0);i++) for(int j=0;j<n.W2.GetLength(1);j++) n.W2[i,j]=v[q++];
		for(int i=0;i<n.B2.Length;i++) n.B2[i]=v[q++];
	}
	static double GaFit(double[] v,NnModel tmp,List<double[]> xs,List<double[]> ys){
		Unpack(tmp,v);
		double sum=0;
		for(int s=0;s<xs.Count;s++){
			double[] y=Forward(tmp,xs[s]);
			double e0=y[0]-ys[s][0],e1=y[1]-ys[s][1];
			sum+=e0*e0+e1*e1;
		}
		return sum/(xs.Count*2.0);
	}
	// GA: популяция весов, турнирный отбор, элитизм, гауссова мутация с затуханием sigma
	public static double TrainGenetic(int popSize=60,int generations=250){
		NnModel n=D.Nn;
		Dictionary<string,List<DateTime>> intakes=MedIntakes();
		List<double[]> xs=new List<double[]>();
		List<double[]> ys=new List<double[]>();
		foreach(Measurement m in D.M.Where(m=>m.Sys>0).OrderByDescending(m=>m.T).Take(2000)){
			xs.Add(FeatsFor(m,intakes));
			ys.Add(new[]{(m.Sys-120.0)/30.0,(m.Dia-80.0)/20.0});
		}
		if(xs.Count<2) return 0;
		int dim=Pack(n).Length;
		NnModel tmp=new NnModel{W1=(double[,])n.W1.Clone(),B1=(double[])n.B1.Clone(),W1b=(double[,])n.W1b.Clone(),B1b=(double[])n.B1b.Clone(),W2=(double[,])n.W2.Clone(),B2=(double[])n.B2.Clone()};
		Random rnd=new Random();
		double[][] pop=new double[popSize][];
		pop[0]=Pack(n);// элита-затравка: текущие веса
		for(int i=1;i<popSize;i++){
			double[] g=new double[dim];
			for(int k=0;k<dim;k++) g[k]=(rnd.NextDouble()*2-1)*0.5;
			pop[i]=g;
		}
		double best=double.MaxValue; double[] bestV=null;
		double sigma=0.25;
		for(int gen=0;gen<generations;gen++){
			double[] fit=new double[popSize];
			for(int i=0;i<popSize;i++) fit[i]=GaFit(pop[i],tmp,xs,ys);
			Array.Sort(fit,pop);
			if(fit[0]<best){best=fit[0];bestV=(double[])pop[0].Clone();}
			double[][] next=new double[popSize][];
			next[0]=pop[0]; next[1]=pop[1];// элитизм
			for(int i=2;i<popSize;i++){
				double[] a=pop[Tournament(fit,rnd)],b=pop[Tournament(fit,rnd)];
				double[] c=(double[])a.Clone();
				for(int k=0;k<dim;k++) if(rnd.NextDouble()<0.5) c[k]=b[k];// равномерный кроссовер
				for(int k=0;k<dim;k++) c[k]+=(rnd.NextDouble()*2-1)*sigma;// мутация
				next[i]=c;
			}
			pop=next;
			sigma=Math.Max(0.01,sigma*0.985);
		}
		if(bestV!=null){
			Unpack(n,bestV);
			n.Loss.Add(new LossPt{T=DateTime.Now.ToString("MM-dd HH:mm"),L=Math.Round(best,4)});
			if(n.Loss.Count>500) n.Loss.RemoveRange(0,n.Loss.Count-500);
		}
		return best;
	}
	static int Tournament(double[] fit,Random rnd){
		int a=rnd.Next(fit.Length),b=rnd.Next(fit.Length),c=rnd.Next(fit.Length);
		int best=fit[a]<=fit[b]?a:b;
		return fit[c]<fit[best]?c:best;
	}

	// ----------------------------- погода -----------------------------
	static string WmoText(int c){
		switch(c){
			case 0:return"ясно";
			case 1:return"малооблачно";
			case 2:return"переменная облачность";
			case 3:return"пасмурно";
			case 45:
			case 48:return"туман";
		}
		if(c>=51&&c<=57) return"морось";
		if(c>=61&&c<=67) return"дождь";
		if(c>=71&&c<=77) return"снег";
		if(c>=80&&c<=82) return"дождевые шквалы";
		if(c==85||c==86) return"снежные шквалы";
		return c>=95?"гроза":"неизвестно";
	}
	public static async void PollWeather(){
		if(_weatherBusy) return;
		_weatherBusy=true;
		ServicePointManager.SecurityProtocol=System.Net.SecurityProtocolType.SystemDefault|SecurityProtocolType.Tls12|SecurityProtocolType.Tls13;
		try{
			string url;
			url=string.Format(CultureInfo.InvariantCulture,"https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,relative_humidity_2m,pressure_msl,weather_code&timezone=auto&forecast_days=2",
			Cfg.Lat,Cfg.Lon);
			JObject j=JObject.Parse(await Http.GetStringAsync(url));
			Weather.PressMm=(float)((double)j["current"]["pressure_msl"]*0.750062);
			Weather.TempC=(float)(double)j["current"]["temperature_2m"];
			Weather.Hum=(float)(double)j["current"]["relative_humidity_2m"];
			Weather.Text=WmoText((int)(double)j["current"]["weather_code"]);
			try{
				if(!Http.DefaultRequestHeaders.Contains("User-Agent"))
					Http.DefaultRequestHeaders.Add("User-Agent",
					"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0");
				if(!Http.DefaultRequestHeaders.Contains("Accept")) Http.DefaultRequestHeaders.Add("Accept","application/json");
				// необязательно, но полезно против WAF/бот-защиты
				if(!Http.DefaultRequestHeaders.Contains("Accept-Language")) Http.DefaultRequestHeaders.Add("Accept-Language","en-US,en;q=0.9,de;q=0.8");
				DateTime now=DateTime.UtcNow;
				DateTime start=now.AddHours(-24);// Kp — 3-часовой, сутки даёт 8 значений, хватает
				url=string.Format("https://kp.gfz.de/app/json/?start={0:yyyy-MM-ddTHH:mm:ssZ}&end={1:yyyy-MM-ddTHH:mm:ssZ}&index=Kp",
				start,now);
				string jsonString=await Http.GetStringAsync(url);
				if(string.IsNullOrWhiteSpace(jsonString)) return;
				JToken token=JToken.Parse(jsonString);
				JToken kpArr=token["Kp"];
				JToken dtArr=token["datetime"];
				if(kpArr is JArray karr&&dtArr is JArray darr){
					double currentKp=3.3;// сентинел-дефолт (Kp 3.3)
					DateTime latestTime=DateTime.MinValue;
					int n=Math.Min(karr.Count,darr.Count);
					for(int i=0;i<n;i++){
						DateTime t;
						if(!DateTime.TryParse((string)darr[i],CultureInfo.InvariantCulture,
							DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,
							out t)||t<=latestTime) continue;
						latestTime=t;
						double.TryParse((string)karr[i],NumberStyles.Number,
						CultureInfo.InvariantCulture,out currentKp);
					}
					Weather.Kp=(int)Math.Round(currentKp*10);// десятые доли
				}
			} catch(Exception ex){ System.Diagnostics.Debug.WriteLine($"Ошибка получения Kp: {ex.Message}"); }

			// HomeAssistant (опционально): перекрывает указанные датчики
			if(!string.IsNullOrWhiteSpace(Cfg.Ha.Url)&&!string.IsNullOrWhiteSpace(Cfg.Ha.Token))
				foreach(KeyValuePair<string,string> kv in Cfg.Ha.Sensors){
					if(string.IsNullOrWhiteSpace(kv.Value)) continue;
					try{
						HttpRequestMessage req=new HttpRequestMessage(HttpMethod.Get,Cfg.Ha.Url.TrimEnd('/')+"/api/sensor/"+Uri.EscapeDataString(kv.Value)+"/state");
						req.Headers.Add("Authorization","Bearer "+Cfg.Ha.Token);
						HttpResponseMessage resp=await Http.SendAsync(req);
						JObject hj=JObject.Parse(await resp.Content.ReadAsStringAsync());
						double v=(double)hj["state"];
						switch(kv.Key){
							case"pressure":Weather.PressMm=(float)(v<1000?v*0.750062:v); break;
							case"temp":Weather.TempC=(float)v; break;
							case"humidity":Weather.Hum=(float)v; break;
							case"kp":Weather.Kp=(int)Math.Round(v*10); break;// к десятых долям
						}
					} catch{}
				}
			Weather.Updated=DateTime.Now;
			MaybePopup("погода");
		} catch{} finally{ _weatherBusy=false; }
	}

	// ----------------------------- историческая погода (архив) -----------------------------
	static void EnsureBrowserHeaders(){
		if(!Http.DefaultRequestHeaders.Contains("User-Agent"))
			Http.DefaultRequestHeaders.Add("User-Agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0");
		if(!Http.DefaultRequestHeaders.Contains("Accept")) Http.DefaultRequestHeaders.Add("Accept","application/json");
		if(!Http.DefaultRequestHeaders.Contains("Accept-Language")) Http.DefaultRequestHeaders.Add("Accept-Language","en-US,en;q=0.9,de;q=0.8");
	}
	public class WxDay{
		public DateTime[] Times;
		public double?[] Pres,Temp,Hum,Code;
		public List<KeyValuePair<DateTime,double>> Kp=new List<KeyValuePair<DateTime,double>>();// старт 3-ч.bin (UTC), значение
	}
	static double?[] PickHourly(JObject h,string key,int n){
		double?[] r=new double?[n];
		JArray a=h[key] as JArray;
		if(a!=null) for(int i=0;i<n&&i<a.Count;i++) r[i]=(double?)a[i];
		return r;
	}
	static WxDay FetchWxDay(DateTime date){
		try{ return FetchWxDayAsync(date).GetAwaiter().GetResult(); } catch{ return null; }
	}
	static async Task<WxDay> FetchWxDayAsync(DateTime date){
		ServicePointManager.SecurityProtocol=System.Net.SecurityProtocolType.SystemDefault|SecurityProtocolType.Tls12|SecurityProtocolType.Tls13;
		EnsureBrowserHeaders();
		int daysBack=(int)(DateTime.Today-date.Date).TotalDays;
		string url;
    		if(daysBack<=5)// свежий период: forecast API (архив ERA5 отстаёт на ~5 суток)
			url=string.Format(CultureInfo.InvariantCulture,"https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&hourly=pressure_msl,temperature_2m,relative_humidity_2m,weather_code&timezone=auto&past_days={2}&forecast_days=1",Cfg.Lat,Cfg.Lon,Math.Min(91,Math.Max(0,daysBack+1)));
		else// глубже — архив ERA5: полный исторический ряд без ограничений past_days
			url=string.Format(CultureInfo.InvariantCulture,"https://archive-api.open-meteo.com/v1/archive?latitude={0}&longitude={1}&start_date={2:yyyy-MM-dd}&end_date={3:yyyy-MM-dd}&hourly=pressure_msl,temperature_2m,relative_humidity_2m,weather_code&timezone=auto",Cfg.Lat,Cfg.Lon,date.Date,date.Date);
		JObject j=JObject.Parse(await Http.GetStringAsync(url));
		JObject h=j["hourly"] as JObject; if(h==null) return null;
		JArray tt=h["time"] as JArray; if(tt==null||tt.Count==0) return null;
		WxDay d=new WxDay();
		int n=tt.Count;
		d.Times=tt.Select(x=>DateTime.Parse((string)x,CultureInfo.InvariantCulture,DateTimeStyles.None)).ToArray();
		d.Pres=PickHourly(h,"pressure_msl",n); d.Temp=PickHourly(h,"temperature_2m",n);
		d.Hum=PickHourly(h,"relative_humidity_2m",n); d.Code=PickHourly(h,"weather_code",n);
		try{// Kp за сутки: 3-часовые бины GFZ
			string kurl=string.Format(CultureInfo.InvariantCulture,"https://kp.gfz.de/app/json/?start={0:yyyy-MM-ddTHH:mm:ssZ}&end={1:yyyy-MM-ddTHH:mm:ssZ}&index=Kp",date.Date.AddDays(-1),date.Date.AddDays(2));
			JObject kj=JObject.Parse(await Http.GetStringAsync(kurl));
			JArray ka=kj["Kp"] as JArray,kd=kj["datetime"] as JArray;
			if(ka!=null&&kd!=null){
				int n2=Math.Min(ka.Count,kd.Count);
				for(int q=0;q<n2;q++){
					DateTime bt; double kv;
					if(DateTime.TryParse((string)kd[q],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out bt)
						&&double.TryParse((string)ka[q],NumberStyles.Number,CultureInfo.InvariantCulture,out kv))
						d.Kp.Add(new KeyValuePair<DateTime,double>(bt,kv));
				}
			}
		}catch{}
		return d;
	}
	// дозаполняет пустые поля погоды/Kp записи по архиву за m.T; true = что-то заполнено
	public static bool FillWx(Measurement m,WxDay d=null){
		if(m.PressMm!=null&&m.TempC!=null&&m.Hum!=null&&m.Kp!=null) return false;
		if(d==null){ d=FetchWxDay(m.T.Date); if(d==null) return false; }
		int idx=-1; double best=double.MaxValue;
		for(int i=0;i<d.Times.Length;i++){ double dd=Math.Abs((d.Times[i]-m.T).TotalMinutes); if(dd<best){best=dd;idx=i;} }
		if(idx<0) return false;
		bool any=false;
		if(m.PressMm==null&&d.Pres[idx]!=null){ m.PressMm=(float)(d.Pres[idx].Value*0.750062); any=true; }
		if(m.TempC==null&&d.Temp[idx]!=null){ m.TempC=(float)d.Temp[idx].Value; any=true; }
		if(m.Hum==null&&d.Hum[idx]!=null){ m.Hum=(float)d.Hum[idx].Value; any=true; }
		if(string.IsNullOrEmpty(m.Wx)&&d.Code[idx]!=null){ m.Wx=WmoText((int)d.Code[idx].Value); any=true; }
		if(m.Kp==null){
			DateTime u=m.T.ToUniversalTime(); double? kv=null;
			foreach(KeyValuePair<DateTime,double> p in d.Kp) if(p.Key<=u&&u<p.Key.AddHours(3)) kv=p.Value;
			if(kv==null){ DateTime lat=DateTime.MinValue; foreach(KeyValuePair<DateTime,double> p in d.Kp) if(p.Key<=u&&p.Key>lat){lat=p.Key;kv=p.Value;} }
			if(kv!=null){ m.Kp=(int)Math.Round(kv.Value*10); any=true; }
		}
		return any;
	}
	// массовое дозаполнение: все записи, где хоть одно поле пусто
	public static int BackfillWeather(DateTime? forceUntil=null){
		List<Measurement> targets=D.M.Where(m=>m.PressMm==null||m.TempC==null||m.Hum==null||m.Kp==null
			||(forceUntil!=null&&m.T<=forceUntil.Value)).OrderBy(m=>m.T).ToList();
		int n=0;
		foreach(IGrouping<DateTime,Measurement> grp in targets.GroupBy(m=>m.T.Date)){
			WxDay d=FetchWxDay(grp.Key); if(d==null) continue;
			foreach(Measurement m in grp){
				if(forceUntil!=null&&m.T<=forceUntil.Value){// принудительно: пишем заново поверх старого снимка
					float? op=m.PressMm,ot=m.TempC,oh=m.Hum; int? okp=m.Kp; string ow=m.Wx;
					m.PressMm=null;m.TempC=null;m.Hum=null;m.Kp=null;m.Wx="";
					bool got=false; try{ got=FillWx(m,d); }catch{}
					if(got){ AppendMeasurement(m); n++; }
					else{ m.PressMm=op;m.TempC=ot;m.Hum=oh;m.Kp=okp;m.Wx=ow; }// архив недоступен — оставляем как было
					System.Threading.Thread.Sleep(120);
					continue;
				}
				try{ if(FillWx(m,d)){ AppendMeasurement(m); n++; } }catch{}
				System.Threading.Thread.Sleep(120);// щадим API
			}
		}
		return n;
	}

	// ----------------------------- popup (прогноз выше нормы) -----------------------------
	public static Control UiCtx;// hidden form для Invoke
	public static void MaybePopup(string trigger){
		if(Weather.PressMm==null) return;
		lock(Sync){
		try{
			if(D.LastPopup!=null&&DateTime.Now-D.LastPopup.Value<TimeSpan.FromMinutes(Cfg.PopupCooldownMin)) return;
			(double sys,double dia) p=PredictNow();
			if(!(p.sys>Cfg.NormSys)&&!(p.dia>Cfg.NormDia)) return;
			D.LastPopup=DateTime.Now;
			AppendPopup();
			string msg="Внимание! Обнаружены внешние условия, схожие с повышением давления.\r\n"+$"Прогноз (личная модель): {p.sys:0} / {p.dia:0} мм рт.ст. (норма ≤ {Cfg.NormSys} / {Cfg.NormDia})\r\n"+$"Погода: {Weather.Text}, {Weather.PressMm?.ToString("0")} мм рт.ст., {Weather.TempC?.ToString("0")}°C. Рекомендуется выполнить измерение.";
			NotifyIcon ni=Program.TrayIcon;
			ni?.ShowBalloonTip(10000,"BpMonitor — внимание",msg,ToolTipIcon.Warning);
		} catch{}
		}
	}

	// ----------------------------- автозапуск (HKCU\...\Run) -----------------------------
	public static bool GetAutostart(){
		try{
			using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return k!=null&&k.GetValue("BpMonitor")!=null;
		} catch{ return false; }
	}
	public static void SetAutostart(bool on){
		try{
			using(RegistryKey k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true)){
				if(k==null) return;
				if(on){
					string exe="";
					try{
						ProcessModule processModule=Process.GetCurrentProcess().MainModule;
						if(processModule!=null) exe=processModule.FileName;
					} catch{ exe=Assembly.GetEntryAssembly()?.Location; }
					k.SetValue("BpMonitor","\""+exe+"\"");
				}
				else k.DeleteValue("BpMonitor",false);
			}
		} catch{}
		Cfg.Autostart=on;
	}

	// ----------------------------- mp3 плеер (зацикленный) -----------------------------
	static IWavePlayer _player;
	static IWaveProvider _loop;
	class LoopProvider:IWaveProvider{
		private readonly RawSourceWaveStream _stream;
		public LoopProvider(RawSourceWaveStream stream){this._stream=stream;}
		public WaveFormat WaveFormat=>_stream.WaveFormat;
		public int Read(byte[] buffer,int offset,int count){
			int total=0;
			while(total<count){
				int r=_stream.Read(buffer,offset+total,count-total);
				if(r==0){
					_stream.Position=0;
					r=_stream.Read(buffer,offset+total,count-total);
				}
				if(r==0) return total;
				total+=r;
			}
			return total;
		}
	}
	public static void StartAlarm(){
		StopAlarm();
		var path=Path.Combine(ExeDir,Cfg.AlarmFile);
		if(!File.Exists(path)) return;
		try{
			byte[] pcm;
			WaveFormat fmt;
			using(var ms=new MemoryStream()){
				using(var mf=new MediaFoundationReader(path)){
					fmt=mf.WaveFormat;
					using(var w=new WaveFileWriter(ms,fmt)){
						var buf=new byte[65536];
						int r;
						while((r=mf.Read(buf,0,buf.Length))>0) w.Write(buf,0,r);
					}
				}
				pcm=ms.ToArray();
			}
			_loop=new LoopProvider(new RawSourceWaveStream(new MemoryStream(pcm),fmt));
			_player=new WaveOutEvent();
			_player.Init(_loop);
			_player.Play();
		} catch{}
	}
	public static void StopAlarm(){
		try{
			if(_player!=null){
				_player.Stop();
				_player.Dispose();
			}
		} catch{}
		_player=null;
		_loop=null;
	}

	// ----------------------------- будильник -----------------------------
	static readonly HashSet<string> Fired=new HashSet<string>();
	static readonly HashSet<string> Announced=new HashSet<string>();
	public static bool AlarmRinging=>_player!=null;
	// должен ли будильник сработать сегодня с учётом расписания
	static bool FireToday(AlarmDef a,DateTime now){
		switch(a.Mode){
			case"days":{
				int iso=(int)now.DayOfWeek;if(iso==0)iso=7;// вс=7
				return a.Days!=null&&a.Days.Contains(iso);
			}
			case"everyN":{
				int n=Math.Max(1,a.EveryN);
				if(n<=1)return true;
				DateTime anchor;
				if(string.IsNullOrWhiteSpace(a.Anchor)||!DateTime.TryParseExact(a.Anchor,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out anchor))return true;// первый раз: срабатываем сегодня и фиксируем якорь
				return(now.Date-anchor.Date).Days%n==0;
			}
			default:return true;
		}
	}
	public static void CheckAlarms(NotifyIcon ni){
		DateTime now=DateTime.Now;
		string today=now.ToString("yyyyMMdd");
		Fired.RemoveWhere(k=>!k.StartsWith(today));// чистим вчерашние ключи
		Announced.RemoveWhere(k=>!k.StartsWith(today));
		for(int i=0;i<Cfg.Alarms.Count;i++){
			AlarmDef a=Cfg.Alarms[i];
			if(!a.Enabled||string.IsNullOrWhiteSpace(a.Time))continue;
			string[] parts=a.Time.Split(':');
			if(parts.Length!=2)continue;
			if(!int.TryParse(parts[0],out int hh)||!int.TryParse(parts[1],out int mm))continue;
			if(!FireToday(a,now))continue;
			string key=today+"_"+i+"_"+a.Time;
			DateTime target=now.Date.AddHours(hh).AddMinutes(mm);
			// догоняем пропущенные (сон/лаги): окно 30 минут после срабатывания
			if(!Fired.Contains(key)&&now>=target&&now-target<=TimeSpan.FromMinutes(30)){
				Fired.Add(key);
				if(a.Mode=="everyN"&&string.IsNullOrWhiteSpace(a.Anchor)){a.Anchor=today;try{SaveCfg();}catch{}}// фиксируем старт цикла «каждые N дней»
				StartAlarm();
				ni.ShowBalloonTip(4000,"BpMonitor","Будильник: "+(a.Label??a.Time),ToolTipIcon.Info);
			}
			if(Cfg.AlarmAheadMin<=0)continue;
			DateTime ahead=target-TimeSpan.FromMinutes(Cfg.AlarmAheadMin);
			if(!Announced.Contains(key)&&now>=ahead&&now<target){
				Announced.Add(key);
				ni.ShowBalloonTip(4000,"BpMonitor","Через "+Cfg.AlarmAheadMin+" мин будильник: "+(a.Label??a.Time),ToolTipIcon.Info);
			}
		}
	}

	// ----------------------------- контекст записи -----------------------------
	public static void Snapshot(Measurement m){
		// кэш старше 2*интервала опроса считаем протухшим: пишем пустоту и обновляем погоду на будущее
		bool stale=Weather.Updated==null||DateTime.Now-Weather.Updated.Value>TimeSpan.FromMinutes(Math.Max(1,Cfg.PollMin)*2);
		if(stale){ m.PressMm=null;m.TempC=null;m.Hum=null;m.Kp=null;m.Wx="";PollWeather(); }
		else{ m.PressMm=Weather.PressMm;m.TempC=Weather.TempC;m.Hum=Weather.Hum;m.Kp=Weather.Kp;m.Wx=Weather.Text; }
		SnapshotContext(m);
	}
	// контекст (эмоция/шум/сон) с окном +10 мин после измерения — вызывается и при добавлении записей журнала
	public static void SnapshotContext(Measurement m){
		EmotionEntry e=D.E.Where(x=>x.T<=m.T.AddMinutes(10)&&m.T-x.T<=TimeSpan.FromHours(12)).OrderByDescending(x=>x.T).FirstOrDefault();
		m.EmotionV=e==null?(float?)null:e.Val;
		m.EmotionLabel=e==null?"":DataStore.EmotionLabel(e.Val);
		m.Noise=D.N.Where(x=>x.T<=m.T.AddMinutes(10)&&m.T-x.T<=TimeSpan.FromHours(3)).Select(x=>x.Type).Distinct().ToList();
		SleepEntry s=D.S.Where(x=>x.T<=m.T.AddMinutes(10)&&m.T-x.T<=TimeSpan.FromHours(36)).OrderByDescending(x=>x.T).FirstOrDefault();
		m.SleepH=s?.Hours;
		m.SleepQ=s?.Quality;
	}

	// ----------------------------- экспорт -----------------------------
	static string CsvLine(params object[] vals){
		return string.Join(";",vals.Select(v=>{
			string s=v==null?"":Convert.ToString(v,CultureInfo.InvariantCulture)?.Replace("\"","\"\"");
			return s!=null&&(s.Contains(";")||s.Contains("\"")||s.Contains("\n"))?"\""+s+"\"":s;
		}));
	}
	private static int? JInt(JObject f,string key){
		if(f[key]==null) return null;
		return int.TryParse(f[key].ToString().Trim(),out int i)?(int?)i:null;
	}
	// JToken -> DateTime: Newtonsoft сам превращает ISO-строки с секундами в Date JValue,
	// а .ToString() у него идёт через текущую культуру (ru: "11.06.2026") -> инвариантный парсер меняет день/месяц
	static DateTime JT(JObject jo,string key){
		JToken t=jo[key];
		if(t==null) throw new FormatException("нет поля "+key);
		if(t.Type==JTokenType.Date) return (DateTime)t;
		return DateTime.Parse(t.ToString(),CultureInfo.InvariantCulture,DateTimeStyles.None);
	}
	static DateTime? JDate(JObject f,string key){
		if(f[key]==null) return null;
		string s=f[key].ToString().Trim();
		if(s=="") return null;
		DateTime d;
		return DateTime.TryParse(s,out d)?(DateTime?)d:null;
	}
	public static List<Measurement> Filtered(JObject f){
		List<Measurement> list=D.M.ToList()??throw new ArgumentNullException("D.M.ToList()");
		DateTime? from=JDate(f,"from");
		DateTime? to=JDate(f,"to");
		if(from!=null) list=list.Where(m=>m.T>=from.Value).ToList();
		if(to!=null) list=list.Where(m=>m.T<=to.Value.AddSeconds(86399)).ToList();
		int? sm=JInt(f,"sysMin");
		int? dm=JInt(f,"diaMin");
		int? pm=JInt(f,"pulseMin");
		if(sm!=null) list=list.Where(m=>m.Sys>=sm).ToList();
		if(dm!=null) list=list.Where(m=>m.Dia>=dm).ToList();
		if(pm!=null) list=list.Where(m=>m.Pulse>=pm).ToList();
		if(f["onlyMeds"]!=null&&(bool)f["onlyMeds"]) list=list.Where(m=>m.Meds.Count>0).ToList();
		return list.OrderBy(m=>m.T).ToList();// в отчётах — старые сверху
	}
	static List<Dictionary<string,object>> DoctorRows(List<Measurement> list){
		List<Dictionary<string,object>> rows=new List<Dictionary<string,object>>();
		int maxPerDay=list.GroupBy(m=>m.T.Date).Select(g=>g.Count()).DefaultIfEmpty(0).Max();
		maxPerDay=Math.Min(Math.Max(maxPerDay,6),8);
		foreach(var day in list.GroupBy(m=>m.T.Date).OrderBy(g=>g.Key)){
			List<Measurement> ms=day.OrderBy(m=>m.T).ToList();
			Dictionary<string,object> r=new Dictionary<string,object>{{"Дата",day.Key.ToString("dd.MM.yyyy")}};
			for(int i=1;i<=maxPerDay;i++){
				Measurement m=ms.Count>=i?ms[i-1]:null;
				r["Изм."+i]=m==null?"":$"{m.T:HH:mm} — {m.Sys}/{m.Dia}, п:{m.Pulse}{(m.Meds.Count>0?" ["+string.Join(",",m.Meds)+"]":"")}";
			}
			r["Давл. мм (мин–макс)"]=ms.Any(m=>m.PressMm.HasValue)?$"{ms.Min(m=>m.PressMm??0)}–{ms.Max(m=>m.PressMm??0)}":"";
			r["Темп. °C (мин–макс)"]=ms.Any(m=>m.TempC.HasValue)?$"{ms.Min(m=>m.TempC??0)}–{ms.Max(m=>m.TempC??0)}":"";
			r["Kp (макс)"]=ms.Any(m=>m.Kp.HasValue)?((ms.Max(m=>m.Kp??0)/10.0).ToString("0.0",CultureInfo.InvariantCulture)):"";
			r["Эмоции"]=string.Join("; ",ms.Where(m=>m.EmotionV.HasValue).Select(m=>$"{m.T:HH:mm} {m.EmotionLabel}").Distinct());
			r["Шум/события"]=string.Join("; ",ms.SelectMany(m=>m.Noise).Distinct());
			r["Сон"]=ms.Any(m=>m.SleepH.HasValue)?$"{ms.Min(m=>m.SleepH??0)} ч (кач. {ms.Max(m=>m.SleepQ??0)})":"";
			rows.Add(r);
		}
		return rows;
	}
	static List<Dictionary<string,object>> RowRows(List<Measurement> list){
		List<Dictionary<string,object>> rows=new List<Dictionary<string,object>>();
		foreach(Measurement m in list)
			rows.Add(new Dictionary<string,object>{
				{"Дата",m.T.ToString("dd.MM.yyyy")},{"Время",m.T.ToString("HH:mm")},
				{"Сист.",m.Sys},{"Диаст.",m.Dia},{"Пульс",m.Pulse},
				{"Давл. мм",m.PressMm?.ToString("0")??""},{"Темп. °C",m.TempC?.ToString("0")??""},
				{"Влажн. %",m.Hum?.ToString("0")??""},{"Kp",m.Kp!=null?(m.Kp.Value/10.0).ToString("0.0",CultureInfo.InvariantCulture):""},{"Погода",m.Wx},
				{"Лекарства",string.Join(", ",m.Meds)},{"Эмоция",m.EmotionLabel},
				{"Шум",string.Join("; ",m.Noise)},{"Сон ч",m.SleepH?.ToString("0.0")??""},
				{"Сон кач.",m.SleepQ?.ToString()??""},{"Прим.",m.Note}
			});
		return rows;
	}
	static List<Dictionary<string,object>> DayRows(List<Measurement> list){
		List<Dictionary<string,object>> rows=new List<Dictionary<string,object>>();
		foreach(IGrouping<DateTime,Measurement> day in list.GroupBy(m=>m.T.Date).OrderBy(g=>g.Key)){
			List<Measurement> ms=day.ToList();
			rows.Add(new Dictionary<string,object>{
				{"Дата",day.Key.ToString("dd.MM.yyyy")},{"Кол-во",ms.Count},
				{"Сред. сист.",Math.Round(ms.Average(m=>m.Sys),1)},{"Сред. диаст.",Math.Round(ms.Average(m=>m.Dia),1)},
				{"Сред. пульс",Math.Round(ms.Average(m=>m.Pulse),1)},
				{"Давл. мм",ms.Any(m=>m.PressMm.HasValue)?string.Format("{0}–{1}",ms.Min(m=>m.PressMm??0),ms.Max(m=>m.PressMm??0)):""},
				{"Темп. °C",ms.Any(m=>m.TempC.HasValue)?string.Format("{0}–{1}",ms.Min(m=>m.TempC??0),ms.Max(m=>m.TempC??0)):""},
				{"Kp макс",ms.Any(m=>m.Kp.HasValue)?((ms.Max(m=>m.Kp??0)/10.0).ToString("0.0",CultureInfo.InvariantCulture)):""},
				{"Лекарства",string.Join(", ",ms.SelectMany(m=>m.Meds).Distinct())},
				{"Эмоции",string.Join("; ",ms.Where(m=>m.EmotionLabel!="").Select(m=>m.EmotionLabel).Distinct())},
				{"Шум",string.Join("; ",ms.SelectMany(m=>m.Noise).Distinct())},
				{"Сон ч",ms.Any(m=>m.SleepH.HasValue)?ms.Min(m=>m.SleepH??0).ToString("0.0"):""}
			});
		}
		return rows;
	}
	public static byte[] BuildExport(JObject f,string kind,string fmt,out string filename,out string ctype){
		ctype="text/csv";
		List<Measurement> list=Filtered(f);
		DateTime from=list.Count>0?list.First().T:DateTime.Now.AddDays(-7);
		DateTime to=list.Count>0?list.Last().T:DateTime.Now;
		string tag=from.ToString("yyyyMMdd")+"_"+to.ToString("yyyyMMdd");
		if(fmt=="xlsx"){
			List<Dictionary<string,object>> rows=kind=="doctor"?DoctorRows(list):
				kind=="days"?DayRows(list):RowRows(list);
			string name="bp_"+kind+"_"+tag+".xlsx";
			string p=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+"_"+name);
			MiniExcel.SaveAs(p,rows);
			byte[] b=File.ReadAllBytes(p);
			File.Delete(p);
			filename=name;
			ctype="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
			return b;
		}
		filename="bp_"+kind+"_"+tag+".csv";
		StringBuilder sb=new StringBuilder("\uFEFF");
		switch(kind){
			case"doctor":{
				List<Dictionary<string,object>> dr=DoctorRows(list);
				if(dr.Count>0){
					sb.AppendLine(CsvLine(dr.First().Keys.ToArray<object>()));
					foreach(var r in dr) sb.AppendLine(CsvLine(r.Values.ToArray()));
				}
				break;
			}
			case"days":{
				List<Dictionary<string,object>> dr=DayRows(list);
				if(dr.Count>0){
					sb.AppendLine(CsvLine(dr.First().Keys.ToArray()));
					foreach(var r in dr) sb.AppendLine(CsvLine(r.Values.ToArray()));
				}
				break;
			}
			default:{
				sb.AppendLine(CsvLine("Дата","Время","Сист","Диаст","Пульс","Давл.мм","Темп.°C","Влажн.%","Kp","Погода","Лекарства","Эмоция","Шум","Сон.ч","Прим."));
				foreach(var m in list)
					sb.AppendLine(CsvLine(m.T.ToString("dd.MM.yyyy"),m.T.ToString("HH:mm"),m.Sys,m.Dia,m.Pulse,
					m.PressMm?.ToString("0")??"",m.TempC?.ToString("0")??"",m.Hum?.ToString("0")??"",
					m.Kp?.ToString()??"",m.Wx,string.Join(", ",m.Meds),m.EmotionLabel,
					string.Join("; ",m.Noise),m.SleepH?.ToString("0.0")??"",m.Note));
				break;
			}
		}
		return Encoding.UTF8.GetBytes(sb.ToString());
	}

	// ----------------------------- импорт сна (Mi Band / Mi Fit CSV) -----------------------------
	public static int ImportSleepCsv(string text,out string err,List<SleepEntry> addedList=null){
		err="";
		int added=0;
		Regex dateRe=new Regex(@"(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})|(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})");
		foreach(string rawLine in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)){
			string line=rawLine.Trim();
			if(line=="") continue;
			if(line.IndexOfAny(new[]{'д','Д','a','A'})>=0&&
				(line.IndexOf("date",StringComparison.OrdinalIgnoreCase)>=0||line.IndexOf("дата",StringComparison.OrdinalIgnoreCase)>=0)) continue;// шапка
			Match dm=dateRe.Match(line);
			if(!dm.Success) continue;
			int y,mo,dd;
			if(dm.Groups[1].Success){
				y=int.Parse(dm.Groups[1].Value);
				mo=int.Parse(dm.Groups[2].Value);
				dd=int.Parse(dm.Groups[3].Value);
			}
			else{
				dd=int.Parse(dm.Groups[4].Value);
				mo=int.Parse(dm.Groups[5].Value);
				y=int.Parse(dm.Groups[6].Value);
			}
			if(y<2000) y+=2000;
			DateTime dt;
			try{ dt=new DateTime(y,mo,dd); } catch{ continue; }
			List<string> tokens=line.Split(new[]{',',';','\t','|'},StringSplitOptions.RemoveEmptyEntries).Select(t=>t.Trim().Trim('"')).ToList();
			tokens.RemoveAll(t=>dateRe.IsMatch(t));
			float? hours=null;
			int? qual=null;
			foreach(var t in tokens){
				double v;
				if(t.Contains(":"))// 7:30
				{
					var hp=t.Split(':');
					if(hp.Length==2&&int.TryParse(hp[0],out var hh)&&int.TryParse(hp[1],out var mi)&&hh<=24){
						hours=hh+mi/60f;
						continue;
					}
				}
				if(!double.TryParse(t,NumberStyles.Any,CultureInfo.InvariantCulture,out v)) continue;
				if(hours==null) hours=v>24?(float)(v/60.0):(float)v;// минуты -> часы
				else if(qual==null) qual=v>10?(int)Math.Round(v/10):(int)Math.Round(v);
			}
			if(hours==null||hours.Value<0.2f||hours.Value>24f) continue;
			SleepEntry se=new SleepEntry{Id=Guid.NewGuid().ToString("N"),T=dt,Hours=hours.Value,Quality=qual??5};
			D.S.Add(se);
			if(addedList!=null) addedList.Add(se);
			added++;
		}
		return added;
	}

	// ----------------------------- HTTP-сервер -----------------------------
	static string ReadBody(HttpListenerRequest r){
		using(StreamReader sr=new StreamReader(r.InputStream,Encoding.UTF8)) return sr.ReadToEnd();
	}
	static void Respond(HttpListenerResponse resp,int code,string ctype,byte[] body){
		resp.StatusCode=code;
		resp.ContentType=ctype+"; charset=utf-8";
		resp.ContentLength64=body.Length;
		resp.OutputStream.Write(body,0,body.Length);
		resp.OutputStream.Close();
	}
	static void JsonResp(HttpListenerResponse resp,object o){Respond(resp,200,"application/json",Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(o)));}
	static JObject MaskToken(JObject cfg){
		JToken t;
		if(cfg["Ha"]!=null&&cfg["Ha"]["Token"]!=null&&(t=cfg["Ha"]["Token"]).ToString()!="")
			cfg["Ha"]["Token"]=TokenMask;
		return cfg;
	}
	public static JObject StateJson(){
		(double sys,double dia) p=PredictNow();
		return new JObject{
			["medPk"]=JObject.FromObject(Cfg.Meds.GroupBy(med=>med).ToDictionary(g=>g.Key,g=>{var kt=MedKtFor(g.Key);return new{half=kt.half,dur=kt.dur,known=MedPkDb.Any(kv=>NormMed(g.Key).Contains(kv.Key))};})),
			["cfg"]=MaskToken(JObject.FromObject(Cfg)),
			["autostart"]=GetAutostart(),
			["weather"]=JObject.FromObject(Weather),
			["measurements"]=JArray.FromObject(D.M.OrderByDescending(m=>m.T).ToList()),
			["emotions"]=JArray.FromObject(D.E.OrderByDescending(e=>e.T).ToList()),
			["noises"]=JArray.FromObject(D.N.OrderByDescending(e=>e.T).ToList()),
			["sleeps"]=JArray.FromObject(D.S.OrderByDescending(e=>e.T).ToList()),
			["nn"]=new JObject{
				["W1"]=JArray.FromObject(D.Nn.W1),["B1"]=JArray.FromObject(D.Nn.B1),["W1b"]=JArray.FromObject(D.Nn.W1b),["B1b"]=JArray.FromObject(D.Nn.B1b),				["W2"]=JArray.FromObject(D.Nn.W2),["B2"]=JArray.FromObject(D.Nn.B2),
				["loss"]=JArray.FromObject(D.Nn.Loss.Skip(Math.Max(0,D.Nn.Loss.Count-300))),
				["predict"]=new JObject{["sys"]=Math.Round(p.sys,1),["dia"]=Math.Round(p.dia,1)}
			}
		};
	}
	public static JArray Predictions(){
		JArray arr=new JArray();
		foreach(Measurement m in D.M.OrderBy(m=>m.T)){
			double[] y=Forward(D.Nn,FeatsFor(m));
			arr.Add(new JObject{["id"]=m.Id,["ps"]=Math.Round(Clamp(120+30*y[0],70,250),1),["pd"]=Math.Round(Clamp(80+20*y[1],40,150),1)});
		}
		return arr;
	}
	// localhost-only + same-origin (защита от CSRF/злых страниц)
	static bool IsLocalSameOrigin(HttpListenerRequest req){
		if(!req.IsLocal) return false;
		string origin=req.Headers["Origin"]??req.Headers["Referer"];
		if(string.IsNullOrEmpty(origin)) return true;// curl/нативные клиенты без заголовка
		try{
			string host=new Uri(origin).Host;
			return host=="localhost"||host=="127.0.0.1";
		} catch{ return false; }
	}
	public static void Handle(HttpListenerContext ctx){
		try{
			HttpListenerRequest req=ctx.Request;
			if(!IsLocalSameOrigin(req)){
				Respond(ctx.Response,403,"text/plain",Encoding.UTF8.GetBytes("403 forbidden: foreign origin"));
				return;
			}
			lock(Sync){
			string path=ctx.Request.Url.AbsolutePath;
			if(req.HttpMethod=="GET"){
				switch(path){
					case"/":
					case"/index.html":{
						if(File.Exists(IndexPath)){
							byte[] b=File.ReadAllBytes(IndexPath);
							Respond(ctx.Response,200,"text/html",b);
							return;
						}
						Respond(ctx.Response,200,"text/html",Encoding.UTF8.GetBytes("<h3>index.html не найден рядом с exe</h3>"));
						return;
					}
					case"/api/state":
						JsonResp(ctx.Response,StateJson());
						return;
					case"/api/nn/predictions":
						JsonResp(ctx.Response,Predictions());
						return;
					case"/api/version":
						JsonResp(ctx.Response,new{version="2.0"});
						return;
					default:
						Respond(ctx.Response,404,"text/plain",Encoding.UTF8.GetBytes("404"));
						return;
				}
			}
			string body=ReadBody(req);
			JObject jo=body.Length>0?JObject.Parse(body):new JObject();
			switch(path){
				case"/api/measurement":{
					Measurement m=new Measurement{
						Id=Guid.NewGuid().ToString("N"),
						T=JT(jo,"t"),
						Sys=(int)jo["sys"],Dia=(int)jo["dia"],Pulse=(int)jo["pulse"],
						Note=jo["note"]!=null?jo["note"].ToString():""
					};
					if(jo["meds"] is JArray) m.Meds=jo["meds"].Values<string>().Where(x=>x!="").ToList();
					Snapshot(m);
					if(m.T<=DateTime.Now.AddHours(-2)) FillWx(m);// старая запись: история погоды за этот момент
					D.M.Add(m);
					Train();// каждый введённый результат — основа обучения
					AppendMeasurement(m);
					SaveNn();
					MaybePopup("запись");
					JsonResp(ctx.Response,new{ok=true,id=m.Id});
					return;
				}
				case"/api/measurement/update":{
					Measurement m=D.M.FirstOrDefault(x=>x.Id==jo["id"].ToString());
					if(m!=null){
						m.T=JT(jo,"t");
						m.Sys=(int)jo["sys"];
						m.Dia=(int)jo["dia"];
						m.Pulse=(int)jo["pulse"];
						m.Note=jo["note"]!=null?jo["note"].ToString():"";
						if(m.T<=DateTime.Now.AddHours(-2)) FillWx(m);
						if(jo["meds"] is JArray) m.Meds=jo["meds"].Values<string>().Where(x=>x!="").ToList();
						Train();
						AppendMeasurement(m);
						SaveNn();
					}
					JsonResp(ctx.Response,new{ok=true});
					return;
				}
				case"/api/measurement/delete":{
					Measurement m=D.M.FirstOrDefault(x=>x.Id==jo["id"].ToString());
					if(m!=null){
						D.M.Remove(m);
						Train();
						AppendGuidRec(RecMeasDel,m.Id);
						SaveNn();
					}
					JsonResp(ctx.Response,new{ok=true});
					return;
				}
				case"/api/emotion":{
					EmotionEntry ee=new EmotionEntry{Id=Guid.NewGuid().ToString("N"),T=JT(jo,"t"),Val=(int)jo["val"],Note=jo["note"]!=null?jo["note"].ToString():""};
					D.E.Add(ee);
					AppendEmotion(ee);
					foreach(Measurement mm in D.M.Where(x=>ee.T>=x.T&&ee.T-x.T<=TimeSpan.FromMinutes(10)).ToList()){ SnapshotContext(mm); AppendMeasurement(mm); }
					JsonResp(ctx.Response,new{ok=true});
					return;
				}
				case"/api/noise":{
					NoiseEntry ne=new NoiseEntry{Id=Guid.NewGuid().ToString("N"),T=JT(jo,"t"),Type=jo["type"].ToString(),Note=jo["note"]!=null?jo["note"].ToString():""};
					D.N.Add(ne);
					if(jo["addType"]!=null&&jo["addType"].ToString()!=""&&!Cfg.Noises.Contains(jo["addType"].ToString())){ Cfg.Noises.Add(jo["addType"].ToString()); SaveCfg(); }
					AppendNoise(ne);
					foreach(Measurement mm in D.M.Where(x=>ne.T>=x.T&&ne.T-x.T<=TimeSpan.FromMinutes(10)).ToList()){ SnapshotContext(mm); AppendMeasurement(mm); }
					JsonResp(ctx.Response,new{ok=true});
					return;
				}
				case"/api/sleep":{
					SleepEntry se=new SleepEntry{Id=Guid.NewGuid().ToString("N"),T=JT(jo,"t"),Hours=(float)jo["h"],Quality=(int)jo["q"],Note=jo["note"]!=null?jo["note"].ToString():""};
					D.S.Add(se);
					AppendSleep(se);
					foreach(Measurement mm in D.M.Where(x=>se.T>=x.T&&se.T-x.T<=TimeSpan.FromMinutes(10)).ToList()){ SnapshotContext(mm); AppendMeasurement(mm); }
					JsonResp(ctx.Response,new{ok=true});
					return;
				}
				case"/api/journal/delete":{
					string id=jo["id"].ToString();
					if(D.E.Any(x=>x.Id==id)){ D.E.Remove(D.E.First(x=>x.Id==id)); AppendGuidRec(RecEmoDel,id); }
					else if(D.N.Any(x=>x.Id==id)){ D.N.Remove(D.N.First(x=>x.Id==id)); AppendGuidRec(RecNoiDel,id); }
					else if(D.S.Any(x=>x.Id==id)){ D.S.Remove(D.S.First(x=>x.Id==id)); AppendGuidRec(RecSleepDel,id); }
					JsonResp(ctx.Response,new{ok=true});
					return;
				}
				case"/api/sleep/import":{
					List<SleepEntry> imported=new List<SleepEntry>();
					int n=ImportSleepCsv(jo["csv"].ToString(),out string err,imported);
					if(n>0){
						Train();
						foreach(SleepEntry se in imported) AppendSleep(se);
						SaveNn();
					}
					JsonResp(ctx.Response,new{ok=true,added=n,err=err});
					return;
				}
				case"/api/config":{
					int oldPort=Cfg.Port;
					string oldToken=Cfg.Ha!=null?Cfg.Ha.Token:"";
					// MERGE: обновляются только поля, присутствующие в теле; остальные сохраняются.
					// Клон + PopulateObject: при ошибке в теле текущий конфиг не трогается.
					Config nc=JsonConvert.DeserializeObject<Config>(JsonConvert.SerializeObject(Cfg),new JsonSerializerSettings{ObjectCreationHandling=ObjectCreationHandling.Replace});
					JsonConvert.PopulateObject(body,nc,new JsonSerializerSettings{ObjectCreationHandling=ObjectCreationHandling.Replace});// Replace: массивы из тела ЗАМЕНЯЮТ существующие (Auto дописывал элементы в список -> дубли будильников/лекарств при каждом сохранении)
					if(nc.Ha!=null&&nc.Ha.Token==TokenMask) nc.Ha.Token=oldToken;// пришла маска — оставляем реальный токен
					nc.Meds=nc.Meds.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();// дедуп по названию без учёта регистра
					nc.Noises=nc.Noises.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
					if(nc.MedKt!=null){ Dictionary<string,MedKt> mk=new Dictionary<string,MedKt>(); foreach(KeyValuePair<string,MedKt> kv in nc.MedKt) if(!string.IsNullOrWhiteSpace(kv.Key)) mk[NormMed(kv.Key)]=kv.Value; nc.MedKt=mk; }
					Cfg=nc;
					SaveCfg();
					EnsureNnDim();// список лекарств мог измениться -> размерность входа
					JsonResp(ctx.Response,new{ok=true,restartRequired=Cfg.Port!=oldPort});
					return;
				}
				case"/api/autostart":
					SetAutostart((bool)jo["on"]);
					SaveCfg();
					JsonResp(ctx.Response,new{ok=true});
					return;
				case"/api/alarm/test":
					StartAlarm();
					JsonResp(ctx.Response,new{ok=true});
					return;
				case"/api/alarm/stop":
					StopAlarm();
					JsonResp(ctx.Response,new{ok=true});
					return;
				case"/api/nn/train_ga":{
					double loss=TrainGenetic();
					SaveNn();
					JsonResp(ctx.Response,new{ok=true,loss=Math.Round(loss,4)});
					return;
				}
				case"/api/nn/train":
					Train();
					SaveNn();
					JsonResp(ctx.Response,new{ok=true});
					return;
				case"/api/nn/reset":
					D.Nn=NewNn(Cfg.Nn.Hidden,Cfg.Nn.Hidden2,InDim());					Train();
					SaveNn();
					JsonResp(ctx.Response,new{ok=true});
					return;
				case"/api/weather/backfill":{
					DateTime? fu=null;
					if(jo["force_until"]!=null){ try{ fu=JT(jo,"force_until"); }catch{} }
					int nwx=BackfillWeather(fu);
					if(nwx>0){ Train(); SaveNn(); }
					JsonResp(ctx.Response,new{ok=true,updated=nwx});
					return;
				}
				case"/api/weather/refresh":
					PollWeather();
					JsonResp(ctx.Response,new{ok=true});
					return;
				case"/api/export":{
					string kind=jo["kind"]!=null?jo["kind"].ToString():"rows";
					string fmt=jo["fmt"]!=null?jo["fmt"].ToString():"csv";
					byte[] data;
					string fname,ctype;
					data=BuildExport(jo,kind,fmt,out fname,out ctype);
					ctx.Response.ContentType=ctype+"; charset=utf-8";
					ctx.Response.AddHeader("Content-Disposition","attachment; filename="+fname);
					ctx.Response.ContentLength64=data.Length;
					ctx.Response.OutputStream.Write(data,0,data.Length);
					ctx.Response.OutputStream.Close();
					return;
				}
				default:JsonResp(ctx.Response,new{ok=false,err="unknown path: "+path}); break;
			}
			}// lock(Sync)
		} catch(Exception ex){ Respond(ctx.Response,500,"application/json",Encoding.UTF8.GetBytes("{\"ok\":false,\"err\":\""+ex.Message.Replace("\"","'")+"\"}")); }
	}
}

// ============================== ТРЕЙ + UI-ЦИКЛ ==============================
static class Program{
	public static NotifyIcon TrayIcon;
	// Разовое уведомление для случаев, когда трея ещё нет (второй экземпляр / ошибка порта)
	static void Balloon(string title,string msg,ToolTipIcon icon){
		try{
			using(NotifyIcon ni=new NotifyIcon{Icon=LoadTrayIcon(),Visible=true}){
				ni.ShowBalloonTip(5000,title,msg,icon);
				System.Threading.Thread.Sleep(5500);
			}
		} catch{}
	}
	static Icon LoadTrayIcon(){
		try{
			string p=Path.Combine(App.ExeDirPath,"app.ico");
			if(File.Exists(p))return new Icon(p);
		} catch{}
		return SystemIcons.Application;
	}
	[STAThread]static int Main(string[] args){
		App.InitPaths();
		App.Load();

		// -------- CLI: просмотр нейросети / статистика --------
		if(args.Length>0){
			switch(args[0]){
				case"--print-weights":
					Console.WriteLine(JsonConvert.SerializeObject(App.D.Nn,Formatting.Indented));
					return 0;
				case"--export-weights" when args.Length>1:
					File.WriteAllText(args[1],JsonConvert.SerializeObject(App.D.Nn,Formatting.Indented));
					Console.WriteLine("weights -> "+args[1]);
					return 0;
			}
			if(args[0]=="--stats"){
				Measurement m=App.D.M.OrderByDescending(x=>x.T).FirstOrDefault();
				(double sys,double dia) p=App.PredictNow();
				Console.WriteLine("измерений: "+App.D.M.Count+
								", эмоций: "+App.D.E.Count+", шума: "+App.D.N.Count+", сна: "+App.D.S.Count);
				Console.WriteLine("последнее: "+(m==null?"—":m.T.ToString("dd.MM HH:mm")+" "+m.Sys+"/"+m.Dia+" п:"+m.Pulse));
				Console.WriteLine("прогноз модели: "+p.sys.ToString("0")+"/"+p.dia.ToString("0")+
								" (норма <= "+App.Cfg.NormSys+"/"+App.Cfg.NormDia+")");
				return 0;
			}
			Console.WriteLine("BpMonitor 2.0 — CLI: --print-weights | --export-weights <file> | --stats");
			return 0;
		}

		// -------- GUI --------
		bool firstInstance;
		Mutex singleInstance=new Mutex(true,"Local\\BpMonitor",out firstInstance);
	if(!firstInstance){
		Balloon("BpMonitor","BpMonitor уже запущен (смотрите трей).",ToolTipIcon.Info);
		return 0;
	}
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);
		HiddenForm ctx=new HiddenForm();
		ctx.CreateControl();
		App.UiCtx=ctx;
		if(App.Cfg.Autostart) App.SetAutostart(true);// подстраховка записи в Run
		ContextMenuStrip menu=new ContextMenuStrip();
		menu.Items.Add("Открыть в браузере",null,(s,e)=>OpenBrowser());
		ToolStripMenuItem autoItem=new ToolStripMenuItem("Автозапуск");
		menu.Items.Add(autoItem);
		autoItem.Checked=App.GetAutostart();
		autoItem.CheckedChanged+=(s,e)=>{
			App.SetAutostart(autoItem.Checked);
			App.SaveCfg();
		};
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add("Выход",null,(s,e)=>ExitApp());
		TrayIcon=new NotifyIcon{
			Icon=LoadTrayIcon(),
			Text="BpMonitor",
			ContextMenuStrip=menu,
			Visible=true
		};
		// Клик ЛКМ: будильник звонит -> остановить; иначе открыть интерфейс. Двойной клик — всегда браузер.
		System.Windows.Forms.Timer clickTimer=new System.Windows.Forms.Timer{Interval=250};
		bool clickPending=false;
		TrayIcon.MouseClick+=(s,e)=>{
			if(e.Button!=MouseButtons.Left)return;
			if(clickPending){// двойной клик
				clickPending=false;clickTimer.Stop();
				OpenBrowser();return;
			}
			clickPending=true;clickTimer.Start();
		};
		clickTimer.Tick+=(s,e)=>{
			clickTimer.Stop();clickPending=false;
			if(App.AlarmRinging){
				App.StopAlarm();
				TrayIcon.ShowBalloonTip(2000,"BpMonitor","Будильник остановлен",ToolTipIcon.Info);
			}
			else OpenBrowser();
		};
		HttpListener http=new HttpListener();
		try{
			http.Prefixes.Add("http://localhost:"+App.Cfg.Port+"/");
			http.Start();
		} catch(Exception ex){
			Balloon("BpMonitor — ошибка","Не удалось занять порт "+App.Cfg.Port+": "+ex.Message+". Измените port в config.json и перезапустите.",ToolTipIcon.Error);
			http.Stop();
			ctx.Close();
			return 1;
		}
		Task.Run(delegate{
			while(http.IsListening){
				HttpListenerContext c;
				try{ c=http.GetContext(); } catch{ break; }
				Task.Run(()=>App.Handle(c));
			}
		});
		System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=1000};
		int tick=0,lastPoll=0;
		timer.Tick+=(s,e)=>{
			tick++;
			App.CheckAlarms(TrayIcon);
			if(tick-lastPoll<Math.Max(1,App.Cfg.PollMin)*60) return;
			lastPoll=tick;
			App.PollWeather();
		};
		timer.Start();
		App.PollWeather();
		OpenBrowser();
		Application.Run(ctx);
		timer.Stop();
		http.Stop();
		App.StopAlarm();
		App.Save();
		return 0;
	}
	static void OpenBrowser(){
		try{ Process.Start(new ProcessStartInfo("http://localhost:"+App.Cfg.Port+"/"){UseShellExecute=true}); } catch{}
	}
	static void ExitApp(){
		TrayIcon.Visible=false;
		Application.Exit();
	}
}

// Небольшая скрытая форма: ShowWithoutActivation — protected-свойство Form,
// поэтому выставляется только из производного класса.
class HiddenForm:Form{
	public HiddenForm(){
		ShowInTaskbar=false;
		Opacity=0.81;
		FormBorderStyle=FormBorderStyle.None;
		Width=1;
		Height=1;
		TopMost=true;
		StartPosition=FormStartPosition.Manual;
		Location=new Point(-8000,-8000);
	}
	protected override bool ShowWithoutActivation=>true;
	protected override CreateParams CreateParams{
		get{
			CreateParams cp=base.CreateParams;
			// WS_EX_TOOLWINDOW — убирает из Alt+Tab
			cp.ExStyle|=0x00000080;
			// WS_EX_TRANSPARENT — делает форму «прозрачной» для кликов мыши
			cp.ExStyle|=0x00000020;
			return cp;
		}
	}
}
}