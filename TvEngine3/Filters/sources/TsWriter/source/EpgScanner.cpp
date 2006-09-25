/* 
 *	Copyright (C) 2006 Team MediaPortal
 *	http://www.team-mediaportal.com
 *
 *  This Program is free software; you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation; either version 2, or (at your option)
 *  any later version.
 *   
 *  This Program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 *  GNU General Public License for more details.
 *   
 *  You should have received a copy of the GNU General Public License
 *  along with GNU Make; see the file COPYING.  If not, write to
 *  the Free Software Foundation, 675 Mass Ave, Cambridge, MA 02139, USA. 
 *  http://www.gnu.org/copyleft/gpl.html
 *
 */

#include <windows.h>
#include <commdlg.h>
#include <bdatypes.h>
#include <time.h>
#include <streams.h>
#include <initguid.h>

#include "EpgScanner.h"


extern void LogDebug(const char *fmt, ...) ;

CEpgScanner::CEpgScanner(LPUNKNOWN pUnk, HRESULT *phr) 
:CUnknown( NAME ("MpTsEpgScanner"), pUnk)
{
  m_pCallBack=NULL;
	m_bGrabbing=false;
}

CEpgScanner::~CEpgScanner(void)
{
}

STDMETHODIMP CEpgScanner::SetCallBack(IEpgCallback* callback)
{
  m_pCallBack=callback;
	return S_OK;
}

STDMETHODIMP CEpgScanner::Reset()
{
	CEnterCriticalSection enter(m_section);
	LogDebug("analyzer CEpgScanner::reset");
	m_bGrabbing=false;
	m_epgParser.Reset();
	return S_OK;
}
STDMETHODIMP CEpgScanner::GrabEPG()
{
	CEnterCriticalSection enter(m_section);
	try
	{
		LogDebug("EpgScanner::GrabEPG");
		m_bGrabbing=true;
		m_epgParser.GrabEPG();
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GrabEPG exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::IsEPGReady(BOOL* yesNo)
{
	CEnterCriticalSection enter(m_section);
	try
	{
		*yesNo=m_epgParser.IsEPGReady();
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::IsEPGReady exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::GetEPGChannelCount( ULONG* channelCount)
{
	CEnterCriticalSection enter(m_section);
	try
	{
		//LogDebug("EpgScanner::GetEPGChannelCount");
		*channelCount=m_epgParser.GetEPGChannelCount( );
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GetEPGChannelCount exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::GetEPGEventCount( ULONG channel,  ULONG* eventCount)
{
	CEnterCriticalSection enter(m_section);
	try
	{
		//LogDebug("EpgScanner::GetEPGEventCount");
		*eventCount=m_epgParser.GetEPGEventCount( channel);
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GetEPGEventCount exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::GetEPGChannel( ULONG channel,  WORD* networkId,  WORD* transportid, WORD* service_id  )
{
	CEnterCriticalSection enter(m_section);
	try
	{
		//LogDebug("EpgScanner::GetEPGChannel");
		m_epgParser.GetEPGChannel( channel,  networkId,  transportid, service_id  );
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GetEPGChannel exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::GetEPGEvent( ULONG channel,  ULONG eventid,ULONG* language, ULONG* dateMJD, ULONG* timeUTC, ULONG* duration, char** genre    )
{
	CEnterCriticalSection enter(m_section);
	try
	{
		//LogDebug("EpgScanner::GetEPGEvent");
		m_epgParser.GetEPGEvent( channel,  eventid, language,dateMJD, timeUTC, duration, genre    );
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GetEPGEvent exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::GetEPGLanguage(THIS_ ULONG channel, ULONG eventid,ULONG languageIndex,ULONG* language,char** eventText, char** eventDescription    )
{
	CEnterCriticalSection enter(m_section);
	try
	{
		//LogDebug("EpgScanner::GetEPGLanguage");
		m_epgParser.GetEPGLanguage( channel,  eventid, languageIndex,language,eventText,eventDescription    );
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GetEPGLanguage exception");
	}
	return S_OK;
}

STDMETHODIMP CEpgScanner::GrabMHW()
{
	CEnterCriticalSection enter(m_section);
	try
	{
		//LogDebug("EpgScanner::GrabMHW");
		m_bGrabbing=true;
		m_mhwParser.GrabEPG();
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GrabMHW exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::IsMHWReady(BOOL* yesNo)
{
	CEnterCriticalSection enter(m_section);
	try
	{
		*yesNo=FALSE;
		if ( m_mhwParser.IsEPGReady()  )
		{
			*yesNo=TRUE;
			return S_OK;
		}
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::IsMHWReady exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::GetMHWTitleCount(WORD* count)
{
	CEnterCriticalSection enter(m_section);
	try
	{
		//LogDebug("EpgScanner::GetMHWTitleCount");
		m_mhwParser.GetTitleCount(count);
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GetMHWTitleCount exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::GetMHWTitle(WORD program, WORD* id, WORD* transportId, WORD* networkId, WORD* channelId, WORD* programId, WORD* themeId, WORD* PPV, BYTE* Summaries, WORD* duration, ULONG* dateStart, ULONG* timeStart,char** title,char** programName)
{	
	CEnterCriticalSection enter(m_section);
	try
	{
		//LogDebug("EpgScanner::GetMHWTitle");
		m_mhwParser.GetTitle(program, id, transportId, networkId, channelId, programId, themeId, PPV, Summaries, duration, dateStart,timeStart,title,programName);
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GetMHWTitle exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::GetMHWChannel(WORD channelNr, WORD* channelId,WORD* networkId, WORD* transportId, char** channelName)
{
	CEnterCriticalSection enter(m_section);
	try
	{
		//LogDebug("EpgScanner::GetMHWChannel");
		m_mhwParser.GetChannel(channelNr,channelId, networkId, transportId, channelName);
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GetMHWChannel exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::GetMHWSummary(WORD programId, char** summary)
{
	CEnterCriticalSection enter(m_section);
	try
	{
		//LogDebug("EpgScanner::GetMHWSummary");
		m_mhwParser.GetSummary(programId, summary);
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GetMHWSummary exception");
	}
	return S_OK;
}
STDMETHODIMP CEpgScanner::GetMHWTheme(WORD themeId, char** theme)
{
	try
	{
		CEnterCriticalSection enter(m_section);
		//LogDebug("EpgScanner::GetMHWTheme");
		m_mhwParser.GetTheme(themeId, theme);
	}
	catch(...)
	{
		LogDebug("analyzer CEpgScanner::GetMHWTheme exception");
	}
	return S_OK;
}


void CEpgScanner::OnTsPacket(byte* tsPacket)
{
	try
	{
		if (m_bGrabbing)
		{
      {//criticalsection
			  CEnterCriticalSection enter(m_section);
			  m_epgParser.OnTsPacket(tsPacket);
			  m_mhwParser.OnTsPacket(tsPacket);
			  if (m_epgParser.IsEPGReady() && m_mhwParser.IsEPGReady())
			  {
				  m_bGrabbing=false;
			  }
      }

      if (false==m_bGrabbing)
      {
        if (m_pCallBack!=NULL)
        {
          m_pCallBack->OnEpgReceived();
        }
      }

		}
	}
	catch(...)
	{
		LogDebug("epg exception");
	}
}