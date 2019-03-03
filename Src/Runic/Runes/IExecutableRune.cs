﻿using System.Collections;
using System.Collections.Generic;


namespace EsotericIDE.Runic.Runes {
	public interface IExecutableRune {
		bool Execute(Pointer pointer, IRunicContext context);
		IExecutableRune Register();
	}
}