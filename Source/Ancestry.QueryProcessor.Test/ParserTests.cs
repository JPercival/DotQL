﻿using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Ancestry.QueryProcessor.Parse;

namespace Ancestry.QueryProcessor.Test
{
	[TestClass]
	public class ParserTests
	{
		[TestMethod]
		public void LiteralSelectTest()
		{
			var parser = new Parser();
//			var statement = parser.ParseStatement("select 1, 2.0f, 3.0d, 0x4, $5, '6'");
//			var emitter = new SQLTextEmitter();
//			var actual = emitter.Emit(statement);
//			Assert.AreEqual
//			(
//				@"select 
//	1, 2, 3.0, 0x4, $5, '6';", 
//				actual
//			);
		}

	}
}
