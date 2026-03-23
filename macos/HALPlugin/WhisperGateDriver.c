// WhisperGateDriver.c — Minimal AudioServerPlugin (virtual input-only mic)
// Reads audio from a memory-mapped file written by WhisperGate app.
// Based on BlackHole's proven patterns for timing and IO.

#include <CoreAudio/AudioServerPlugIn.h>
#include <CoreAudio/AudioHardware.h>
#include <CoreFoundation/CoreFoundation.h>
#include <os/log.h>
#include <mach/mach_time.h>
#include <pthread.h>
#include <sys/mman.h>
#include <fcntl.h>
#include <unistd.h>
#include <stdatomic.h>
#include <string.h>
#include <errno.h>

// ============================================================================
// Constants — matching BlackHole's proven values
// ============================================================================

#define SAMPLE_RATE             48000.0
#define kDevice_RingBufferSize  16384   // ZeroTimeStampPeriod (BlackHole uses 16384)
#define kRing_Buffer_Frame_Size 65536   // Actual ring buffer size for IO
#define SHM_FILE_PATH           "/tmp/whispergate_audio.buf"
#define SHM_HEADER_SIZE         64
#define SHM_RING_FRAMES         96000   // 2s ring buffer in the shared file

// ============================================================================
// Shared memory layout (must match SharedRingBuffer.swift)
// ============================================================================

typedef struct {
    _Atomic uint64_t writePosition;
    _Atomic uint64_t readPosition;
    float            sampleRate;
    _Atomic uint32_t isActive;
    uint32_t         padding[9];
} WGSharedHeader;

// ============================================================================
// Object IDs
// ============================================================================

enum {
    kObjectID_PlugIn = kAudioObjectPlugInObject,
    kObjectID_Device = 2,
    kObjectID_Stream = 3,
};

// ============================================================================
// State
// ============================================================================

static AudioServerPlugInHostRef gHost = NULL;
static pthread_mutex_t gStateMutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_mutex_t gIOMutex    = PTHREAD_MUTEX_INITIALIZER;
static UInt32 gRefCount = 1;

// IO state — matches BlackHole exactly
static UInt64   gIORunningCount = 0;
static UInt64   gAnchorHostTime = 0;
static UInt64   gNumberTimeStamps = 0;
static Float64  gPreviousTicks = 0;
static Float64  gHostTicksPerFrame = 0;

// Internal ring buffer (driver-side, like BlackHole's gRingBuffer)
static Float32* gRingBuffer = NULL;

// Shared memory (app writes here, we copy into gRingBuffer)
static WGSharedHeader* gShm = NULL;
static float*          gShmRing = NULL;
static int             gShmFD = -1;

// ============================================================================
// Logging
// ============================================================================

static os_log_t wg_log(void) {
    static os_log_t l = NULL;
    if (!l) l = os_log_create("com.whispergate.driver", "HAL");
    return l;
}

// ============================================================================
// Shared memory — file-based mmap (POSIX shm blocked by sandbox)
// ============================================================================

static void shm_open_if_needed(void) {
    if (gShm) return;
    int fd = open(SHM_FILE_PATH, O_RDWR, 0);
    if (fd < 0) return;
    size_t sz = SHM_HEADER_SIZE + (SHM_RING_FRAMES * sizeof(float));
    void* m = mmap(NULL, sz, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (m == MAP_FAILED) { close(fd); return; }
    gShmFD = fd;
    gShm = (WGSharedHeader*)m;
    gShmRing = (float*)((uint8_t*)m + SHM_HEADER_SIZE);
    os_log(wg_log(), "ring buffer connected");
}

static void shm_close_if_open(void) {
    if (gShm) {
        munmap(gShm, SHM_HEADER_SIZE + (SHM_RING_FRAMES * sizeof(float)));
        gShm = NULL; gShmRing = NULL;
    }
    if (gShmFD >= 0) { close(gShmFD); gShmFD = -1; }
}

// ============================================================================
// Forward ref
// ============================================================================

static AudioServerPlugInDriverRef gDriverRef;

static AudioStreamBasicDescription wg_format(void) {
    AudioStreamBasicDescription f = {0};
    f.mSampleRate = SAMPLE_RATE;
    f.mFormatID = kAudioFormatLinearPCM;
    f.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked;
    f.mBytesPerPacket = 4; f.mFramesPerPacket = 1; f.mBytesPerFrame = 4;
    f.mChannelsPerFrame = 1; f.mBitsPerChannel = 32;
    return f;
}

// ============================================================================
// IUnknown
// ============================================================================

static HRESULT wg_QueryInterface(void* drv, REFIID uuid, LPVOID* out) {
    if (!out) return E_POINTER;
    CFUUIDRef req = CFUUIDCreateFromUUIDBytes(NULL, uuid);
    if (!req) return E_UNEXPECTED;
    if (CFEqual(req, IUnknownUUID) || CFEqual(req, kAudioServerPlugInDriverInterfaceUUID)) {
        CFRelease(req);
        pthread_mutex_lock(&gStateMutex); ++gRefCount; pthread_mutex_unlock(&gStateMutex);
        *out = gDriverRef;
        return S_OK;
    }
    CFRelease(req);
    *out = NULL;
    return E_NOINTERFACE;
}

static ULONG wg_AddRef(void* d) {
    pthread_mutex_lock(&gStateMutex); ULONG r = ++gRefCount; pthread_mutex_unlock(&gStateMutex); return r;
}
static ULONG wg_Release(void* d) {
    pthread_mutex_lock(&gStateMutex); ULONG r = --gRefCount; pthread_mutex_unlock(&gStateMutex); return r;
}

// ============================================================================
// Lifecycle
// ============================================================================

static OSStatus wg_Initialize(AudioServerPlugInDriverRef d, AudioServerPlugInHostRef host) {
    os_log(wg_log(), "Initialize");
    gHost = host;
    // Calculate host ticks per frame — exactly BlackHole's formula
    struct mach_timebase_info tbi;
    mach_timebase_info(&tbi);
    Float64 hostClockFreq = (Float64)tbi.denom / (Float64)tbi.numer;
    hostClockFreq *= 1000000000.0;
    gHostTicksPerFrame = hostClockFreq / SAMPLE_RATE;
    return kAudioHardwareNoError;
}

static OSStatus wg_CreateDevice(AudioServerPlugInDriverRef d, CFDictionaryRef desc, const AudioServerPlugInClientInfo* c, AudioObjectID* out) { return kAudioHardwareUnsupportedOperationError; }
static OSStatus wg_DestroyDevice(AudioServerPlugInDriverRef d, AudioObjectID id) { return kAudioHardwareUnsupportedOperationError; }
static OSStatus wg_AddDeviceClient(AudioServerPlugInDriverRef d, AudioObjectID id, const AudioServerPlugInClientInfo* c) { return kAudioHardwareNoError; }
static OSStatus wg_RemoveDeviceClient(AudioServerPlugInDriverRef d, AudioObjectID id, const AudioServerPlugInClientInfo* c) { return kAudioHardwareNoError; }
static OSStatus wg_PerformConfigChange(AudioServerPlugInDriverRef d, AudioObjectID id, UInt64 a, void* i) { return kAudioHardwareNoError; }
static OSStatus wg_AbortConfigChange(AudioServerPlugInDriverRef d, AudioObjectID id, UInt64 a, void* i) { return kAudioHardwareNoError; }

// ============================================================================
// HasProperty
// ============================================================================

static Boolean wg_HasProperty(AudioServerPlugInDriverRef d, AudioObjectID id, pid_t pid, const AudioObjectPropertyAddress* a) {
    switch (id) {
    case kObjectID_PlugIn:
        switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyManufacturer: case kAudioObjectPropertyOwnedObjects:
        case kAudioPlugInPropertyDeviceList: case kAudioPlugInPropertyTranslateUIDToDevice:
        case kAudioPlugInPropertyResourceBundle: return true;
        default: return false;
        }
    case kObjectID_Device:
        switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyName: case kAudioObjectPropertyManufacturer: case kAudioObjectPropertyOwnedObjects:
        case kAudioDevicePropertyDeviceUID: case kAudioDevicePropertyModelUID:
        case kAudioDevicePropertyTransportType: case kAudioDevicePropertyRelatedDevices:
        case kAudioDevicePropertyClockDomain: case kAudioDevicePropertyDeviceIsAlive:
        case kAudioDevicePropertyDeviceIsRunning: case kAudioObjectPropertyControlList:
        case kAudioDevicePropertyNominalSampleRate: case kAudioDevicePropertyAvailableNominalSampleRates:
        case kAudioDevicePropertyIsHidden: case kAudioDevicePropertyZeroTimeStampPeriod:
        case kAudioDevicePropertyStreams: return true;
        case kAudioDevicePropertyDeviceCanBeDefaultDevice: case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice:
        case kAudioDevicePropertyLatency: case kAudioDevicePropertySafetyOffset:
        case kAudioDevicePropertyPreferredChannelsForStereo: case kAudioDevicePropertyPreferredChannelLayout:
            return a->mScope == kAudioObjectPropertyScopeInput;
        default: return false;
        }
    case kObjectID_Stream:
        switch (a->mSelector) {
        case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass: case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyName: case kAudioStreamPropertyIsActive: case kAudioStreamPropertyDirection:
        case kAudioStreamPropertyTerminalType: case kAudioStreamPropertyStartingChannel:
        case kAudioStreamPropertyVirtualFormat: case kAudioStreamPropertyPhysicalFormat:
        case kAudioStreamPropertyAvailableVirtualFormats: case kAudioStreamPropertyAvailablePhysicalFormats:
            return true;
        default: return false;
        }
    default: return false;
    }
}

static OSStatus wg_IsPropertySettable(AudioServerPlugInDriverRef d, AudioObjectID id, pid_t pid,
    const AudioObjectPropertyAddress* a, Boolean* out) { *out = false; return kAudioHardwareNoError; }

// ============================================================================
// GetPropertyDataSize
// ============================================================================

static OSStatus wg_GetPropertyDataSize(AudioServerPlugInDriverRef d, AudioObjectID id, pid_t pid,
    const AudioObjectPropertyAddress* a, UInt32 qSz, const void* q, UInt32* outSz)
{
    switch (a->mSelector) {
    case kAudioObjectPropertyBaseClass: case kAudioObjectPropertyClass:
        *outSz = sizeof(AudioClassID); return kAudioHardwareNoError;
    case kAudioObjectPropertyOwner: case kAudioObjectPropertyOwnedObjects:
    case kAudioPlugInPropertyDeviceList: case kAudioPlugInPropertyTranslateUIDToDevice:
    case kAudioDevicePropertyRelatedDevices:
        *outSz = sizeof(AudioObjectID); return kAudioHardwareNoError;
    case kAudioObjectPropertyManufacturer: case kAudioObjectPropertyName:
    case kAudioDevicePropertyDeviceUID: case kAudioDevicePropertyModelUID:
    case kAudioPlugInPropertyResourceBundle:
        *outSz = sizeof(CFStringRef); return kAudioHardwareNoError;
    case kAudioObjectPropertyControlList:
        *outSz = 0; return kAudioHardwareNoError;
    case kAudioDevicePropertyStreams:
        *outSz = (a->mScope == kAudioObjectPropertyScopeOutput) ? 0 : sizeof(AudioObjectID);
        return kAudioHardwareNoError;
    case kAudioDevicePropertyNominalSampleRate:
        *outSz = sizeof(Float64); return kAudioHardwareNoError;
    case kAudioDevicePropertyAvailableNominalSampleRates:
        *outSz = sizeof(AudioValueRange); return kAudioHardwareNoError;
    case kAudioDevicePropertyTransportType: case kAudioDevicePropertyDeviceCanBeDefaultDevice:
    case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice: case kAudioDevicePropertyDeviceIsAlive:
    case kAudioDevicePropertyDeviceIsRunning: case kAudioDevicePropertyIsHidden:
    case kAudioDevicePropertyLatency: case kAudioDevicePropertySafetyOffset:
    case kAudioDevicePropertyClockDomain: case kAudioDevicePropertyZeroTimeStampPeriod:
    case kAudioStreamPropertyIsActive: case kAudioStreamPropertyDirection:
    case kAudioStreamPropertyTerminalType: case kAudioStreamPropertyStartingChannel:
        *outSz = sizeof(UInt32); return kAudioHardwareNoError;
    case kAudioDevicePropertyPreferredChannelsForStereo:
        *outSz = 2 * sizeof(UInt32); return kAudioHardwareNoError;
    case kAudioDevicePropertyPreferredChannelLayout:
        *outSz = sizeof(AudioChannelLayout); return kAudioHardwareNoError;
    case kAudioStreamPropertyVirtualFormat: case kAudioStreamPropertyPhysicalFormat:
        *outSz = sizeof(AudioStreamBasicDescription); return kAudioHardwareNoError;
    case kAudioStreamPropertyAvailableVirtualFormats: case kAudioStreamPropertyAvailablePhysicalFormats:
        *outSz = sizeof(AudioStreamRangedDescription); return kAudioHardwareNoError;
    default:
        *outSz = 0; return kAudioHardwareUnknownPropertyError;
    }
}

// ============================================================================
// GetPropertyData
// ============================================================================

#define STR(o,s,v) do{*(CFStringRef*)(o)=CFSTR(v);*(s)=sizeof(CFStringRef);}while(0)
#define U32(o,s,v) do{*(UInt32*)(o)=(v);*(s)=sizeof(UInt32);}while(0)
#define OBJ(o,s,v) do{*(AudioObjectID*)(o)=(v);*(s)=sizeof(AudioObjectID);}while(0)
#define CLS(o,s,v) do{*(AudioClassID*)(o)=(v);*(s)=sizeof(AudioClassID);}while(0)

static OSStatus wg_GetPlugInData(const AudioObjectPropertyAddress* a, UInt32 qSz, const void* q, UInt32* sz, void* o) {
    switch (a->mSelector) {
    case kAudioObjectPropertyBaseClass:     CLS(o,sz,kAudioObjectClassID); break;
    case kAudioObjectPropertyClass:         CLS(o,sz,kAudioPlugInClassID); break;
    case kAudioObjectPropertyOwner:         OBJ(o,sz,kAudioObjectUnknown); break;
    case kAudioObjectPropertyManufacturer:  STR(o,sz,"WhisperGate"); break;
    case kAudioObjectPropertyOwnedObjects:
    case kAudioPlugInPropertyDeviceList:    OBJ(o,sz,kObjectID_Device); break;
    case kAudioPlugInPropertyTranslateUIDToDevice:
        if (q && CFEqual(*(CFStringRef*)q, CFSTR("com.whispergate.virtualmic")))
            OBJ(o,sz,kObjectID_Device);
        else OBJ(o,sz,kAudioObjectUnknown);
        break;
    case kAudioPlugInPropertyResourceBundle: STR(o,sz,""); break;
    default: return kAudioHardwareUnknownPropertyError;
    }
    return kAudioHardwareNoError;
}

static OSStatus wg_GetDeviceData(const AudioObjectPropertyAddress* a, UInt32* sz, void* o) {
    switch (a->mSelector) {
    case kAudioObjectPropertyBaseClass:     CLS(o,sz,kAudioObjectClassID); break;
    case kAudioObjectPropertyClass:         CLS(o,sz,kAudioDeviceClassID); break;
    case kAudioObjectPropertyOwner:         OBJ(o,sz,kObjectID_PlugIn); break;
    case kAudioObjectPropertyName:          STR(o,sz,"WhisperGate Mic"); break;
    case kAudioObjectPropertyManufacturer:  STR(o,sz,"WhisperGate"); break;
    case kAudioObjectPropertyOwnedObjects:
    case kAudioDevicePropertyStreams:
        if (a->mScope == kAudioObjectPropertyScopeOutput) { *sz = 0; }
        else { OBJ(o,sz,kObjectID_Stream); }
        break;
    case kAudioDevicePropertyDeviceUID:     STR(o,sz,"com.whispergate.virtualmic"); break;
    case kAudioDevicePropertyModelUID:      STR(o,sz,"com.whispergate.virtualmic.model"); break;
    case kAudioDevicePropertyTransportType: U32(o,sz,kAudioDeviceTransportTypeVirtual); break;
    case kAudioDevicePropertyRelatedDevices: OBJ(o,sz,kObjectID_Device); break;
    case kAudioDevicePropertyClockDomain:   U32(o,sz,0); break;
    case kAudioDevicePropertyDeviceIsAlive: U32(o,sz,1); break;
    case kAudioDevicePropertyDeviceIsRunning: U32(o,sz,gIORunningCount>0?1:0); break;
    case kAudioObjectPropertyControlList:   *sz = 0; break;
    case kAudioDevicePropertyDeviceCanBeDefaultDevice:  U32(o,sz,0); break;
    case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice: U32(o,sz,0); break;
    case kAudioDevicePropertyIsHidden:      U32(o,sz,0); break;
    case kAudioDevicePropertyLatency:       U32(o,sz,0); break;
    case kAudioDevicePropertySafetyOffset:  U32(o,sz,0); break;
    case kAudioDevicePropertyZeroTimeStampPeriod: U32(o,sz,kDevice_RingBufferSize); break;
    case kAudioDevicePropertyNominalSampleRate:
        *(Float64*)o = SAMPLE_RATE; *sz = sizeof(Float64); break;
    case kAudioDevicePropertyAvailableNominalSampleRates: {
        AudioValueRange r = {SAMPLE_RATE,SAMPLE_RATE};
        memcpy(o,&r,sizeof(r)); *sz = sizeof(r); break; }
    case kAudioDevicePropertyPreferredChannelsForStereo:
        ((UInt32*)o)[0]=1; ((UInt32*)o)[1]=1; *sz=2*sizeof(UInt32); break;
    case kAudioDevicePropertyPreferredChannelLayout: {
        AudioChannelLayout l={0}; l.mChannelLayoutTag=kAudioChannelLayoutTag_Mono;
        memcpy(o,&l,sizeof(l)); *sz=sizeof(l); break; }
    default: return kAudioHardwareUnknownPropertyError;
    }
    return kAudioHardwareNoError;
}

static OSStatus wg_GetStreamData(const AudioObjectPropertyAddress* a, UInt32* sz, void* o) {
    switch (a->mSelector) {
    case kAudioObjectPropertyBaseClass:     CLS(o,sz,kAudioObjectClassID); break;
    case kAudioObjectPropertyClass:         CLS(o,sz,kAudioStreamClassID); break;
    case kAudioObjectPropertyOwner:         OBJ(o,sz,kObjectID_Device); break;
    case kAudioObjectPropertyName:          STR(o,sz,"WhisperGate Input"); break;
    case kAudioStreamPropertyIsActive:      U32(o,sz,1); break;
    case kAudioStreamPropertyDirection:     U32(o,sz,1); break;
    case kAudioStreamPropertyTerminalType:  U32(o,sz,kAudioStreamTerminalTypeMicrophone); break;
    case kAudioStreamPropertyStartingChannel: U32(o,sz,1); break;
    case kAudioStreamPropertyVirtualFormat:
    case kAudioStreamPropertyPhysicalFormat: {
        AudioStreamBasicDescription f=wg_format();
        memcpy(o,&f,sizeof(f)); *sz=sizeof(f); break; }
    case kAudioStreamPropertyAvailableVirtualFormats:
    case kAudioStreamPropertyAvailablePhysicalFormats: {
        AudioStreamRangedDescription rd={0};
        rd.mFormat=wg_format();
        rd.mSampleRateRange.mMinimum=SAMPLE_RATE;
        rd.mSampleRateRange.mMaximum=SAMPLE_RATE;
        memcpy(o,&rd,sizeof(rd)); *sz=sizeof(rd); break; }
    default: return kAudioHardwareUnknownPropertyError;
    }
    return kAudioHardwareNoError;
}

static OSStatus wg_GetPropertyData(AudioServerPlugInDriverRef d, AudioObjectID id, pid_t pid,
    const AudioObjectPropertyAddress* a, UInt32 qSz, const void* q, UInt32 inSz, UInt32* outSz, void* out) {
    switch (id) {
    case kObjectID_PlugIn: return wg_GetPlugInData(a,qSz,q,outSz,out);
    case kObjectID_Device: return wg_GetDeviceData(a,outSz,out);
    case kObjectID_Stream: return wg_GetStreamData(a,outSz,out);
    default: return kAudioHardwareBadObjectError;
    }
}

static OSStatus wg_SetPropertyData(AudioServerPlugInDriverRef d, AudioObjectID id, pid_t pid,
    const AudioObjectPropertyAddress* a, UInt32 qSz, const void* q, UInt32 inSz, const void* in) {
    return kAudioHardwareNoError;
}

// ============================================================================
// IO — copied from BlackHole's proven patterns
// ============================================================================

static OSStatus wg_StartIO(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 clientID) {
    os_log(wg_log(), "StartIO");
    pthread_mutex_lock(&gStateMutex);
    gIORunningCount++;
    if (gIORunningCount == 1 && gRingBuffer == NULL) {
        gNumberTimeStamps = 0;
        gPreviousTicks = 0;
        gAnchorHostTime = mach_absolute_time();
        gRingBuffer = calloc(kRing_Buffer_Frame_Size, sizeof(Float32));
        shm_open_if_needed();
    }
    pthread_mutex_unlock(&gStateMutex);
    return kAudioHardwareNoError;
}

static OSStatus wg_StopIO(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 clientID) {
    os_log(wg_log(), "StopIO");
    pthread_mutex_lock(&gStateMutex);
    if (gIORunningCount > 0) gIORunningCount--;
    if (gIORunningCount == 0 && gRingBuffer != NULL) {
        free(gRingBuffer);
        gRingBuffer = NULL;
        shm_close_if_open();
    }
    pthread_mutex_unlock(&gStateMutex);
    return kAudioHardwareNoError;
}

// Exactly BlackHole's GetZeroTimeStamp logic
static OSStatus wg_GetZeroTimeStamp(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 clientID,
    Float64* outSampleTime, UInt64* outHostTime, UInt64* outSeed)
{
    pthread_mutex_lock(&gIOMutex);

    UInt64 theCurrentHostTime = mach_absolute_time();
    Float64 theHostTicksPerRingBuffer = gHostTicksPerFrame * ((Float64)kDevice_RingBufferSize);
    Float64 theNextTickOffset = gPreviousTicks + theHostTicksPerRingBuffer;
    UInt64 theNextHostTime = gAnchorHostTime + ((UInt64)theNextTickOffset);

    if (theNextHostTime <= theCurrentHostTime) {
        ++gNumberTimeStamps;
        gPreviousTicks = theNextTickOffset;
    }

    *outSampleTime = gNumberTimeStamps * kDevice_RingBufferSize;
    *outHostTime = gAnchorHostTime + (UInt64)gPreviousTicks;
    *outSeed = 1;

    pthread_mutex_unlock(&gIOMutex);
    return kAudioHardwareNoError;
}

static OSStatus wg_WillDoIOOperation(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 clientID,
    UInt32 opID, Boolean* outWillDo, Boolean* outInPlace) {
    *outWillDo = (opID == kAudioServerPlugInIOOperationReadInput);
    *outInPlace = true;
    return kAudioHardwareNoError;
}

static OSStatus wg_BeginIOOperation(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 clientID,
    UInt32 opID, UInt32 frames, const AudioServerPlugInIOCycleInfo* info) {
    return kAudioHardwareNoError;
}

// Copy data from shared mmap into our ring buffer, then serve from ring buffer
// using BlackHole's mSampleTime-based positioning
static OSStatus wg_DoIOOperation(AudioServerPlugInDriverRef d, AudioObjectID id, AudioObjectID streamID,
    UInt32 clientID, UInt32 opID, UInt32 frames, const AudioServerPlugInIOCycleInfo* info,
    void* ioMain, void* ioSecondary)
{
    if (opID != kAudioServerPlugInIOOperationReadInput) return kAudioHardwareNoError;
    if (!gRingBuffer) { memset(ioMain, 0, frames * sizeof(Float32)); return kAudioHardwareNoError; }

    // Try to connect to shared memory if not yet connected
    if (!gShm) shm_open_if_needed();

    // Copy new data from shared mmap into our local ring buffer
    if (gShm && gShmRing) {
        uint64_t wp = atomic_load(&gShm->writePosition);
        uint64_t rp = atomic_load(&gShm->readPosition);
        uint64_t avail = wp - rp;
        if (avail > 0) {
            if (avail > kRing_Buffer_Frame_Size) avail = kRing_Buffer_Frame_Size; // cap
            for (uint64_t i = 0; i < avail; i++) {
                uint64_t srcIdx = (rp + i) % SHM_RING_FRAMES;
                uint64_t dstIdx = (rp + i) % kRing_Buffer_Frame_Size;
                gRingBuffer[dstIdx] = gShmRing[srcIdx];
            }
            atomic_store(&gShm->readPosition, rp + avail);
        }
    }

    // Read from local ring buffer using mSampleTime (BlackHole pattern)
    UInt64 mSampleTime = (UInt64)info->mInputTime.mSampleTime;
    UInt32 startIdx = mSampleTime % kRing_Buffer_Frame_Size;
    UInt32 firstPart = kRing_Buffer_Frame_Size - startIdx;

    if (firstPart >= frames) {
        memcpy(ioMain, gRingBuffer + startIdx, frames * sizeof(Float32));
    } else {
        memcpy(ioMain, gRingBuffer + startIdx, firstPart * sizeof(Float32));
        memcpy((Float32*)ioMain + firstPart, gRingBuffer, (frames - firstPart) * sizeof(Float32));
    }

    return kAudioHardwareNoError;
}

static OSStatus wg_EndIOOperation(AudioServerPlugInDriverRef d, AudioObjectID id, UInt32 clientID,
    UInt32 opID, UInt32 frames, const AudioServerPlugInIOCycleInfo* info) {
    return kAudioHardwareNoError;
}

// ============================================================================
// Vtable and factory
// ============================================================================

static AudioServerPlugInDriverInterface gVtable = {
    NULL,
    wg_QueryInterface, wg_AddRef, wg_Release,
    wg_Initialize, wg_CreateDevice, wg_DestroyDevice,
    wg_AddDeviceClient, wg_RemoveDeviceClient,
    wg_PerformConfigChange, wg_AbortConfigChange,
    wg_HasProperty, wg_IsPropertySettable, wg_GetPropertyDataSize,
    wg_GetPropertyData, wg_SetPropertyData,
    wg_StartIO, wg_StopIO, wg_GetZeroTimeStamp,
    wg_WillDoIOOperation, wg_BeginIOOperation, wg_DoIOOperation, wg_EndIOOperation,
};
static AudioServerPlugInDriverInterface* gVtablePtr = &gVtable;
static AudioServerPlugInDriverRef gDriverRef = &gVtablePtr;

void* WhisperGateDriverFactory(CFAllocatorRef alloc, CFUUIDRef typeUUID) {
    os_log(wg_log(), "Factory");
    if (!CFEqual(typeUUID, kAudioServerPlugInTypeUUID)) return NULL;
    return gDriverRef;
}
