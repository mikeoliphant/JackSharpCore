// Author:
//       Thomas Mayer <thomas@residuum.org>
//
// Copyright (c) 2016 Thomas Mayer
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using JackSharp.Ports;

namespace JackSharp.Events
{
	/// <summary>
	/// Port registration event arguments.
	/// </summary>
	public sealed class PortRegistrationEventArgs : EventArgs
	{
		/// <summary>
		/// Gets the port.
		/// </summary>
		/// <value>The port.</value>
		public PortReference Port { get; private set; }

		/// <summary>
		/// Gets the type of the change.
		/// </summary>
		/// <value>The type of the change.</value>
		public ChangeType ChangeType { get; private set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="JackSharp.Events.PortRegistrationEventArgs"/> class.
		/// </summary>
		/// <param name="port">Port.</param>
		/// <param name="changeType">Change type.</param>
		public PortRegistrationEventArgs (PortReference port, ChangeType changeType)
		{
			Port = port;
			ChangeType = changeType;
		}
	}
}