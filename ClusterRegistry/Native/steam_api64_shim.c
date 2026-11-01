#include <stdint.h>

extern void *SteamInternal_GameServer_Init_V2(uint32_t ip, uint16_t steamPort, uint16_t gamePort, uint16_t queryPort, int serverMode, const char *versionString);

void *SteamInternal_GameServer_Init(uint32_t ip, uint16_t steamPort, uint16_t gamePort, uint16_t queryPort, int serverMode, const char *versionString)
{
	return SteamInternal_GameServer_Init_V2(ip, steamPort, gamePort, queryPort, serverMode, versionString);
}
