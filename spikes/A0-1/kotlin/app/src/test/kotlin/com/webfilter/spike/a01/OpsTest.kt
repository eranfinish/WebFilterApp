package com.webfilter.spike.a01

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class OpsTest {

    @Test
    fun parsesEveryWireName() {
        for (op in Op.ALL) {
            assertEquals(op, Op.parse(op.wire), "wire=${op.wire}")
        }
    }

    @Test
    fun rejectsUnknownAndMissingOps() {
        assertNull(Op.parse(null))
        assertNull(Op.parse(""))
        assertNull(Op.parse("CREATE"))
        assertNull(Op.parse("bogus"))
    }

    @Test
    fun wireNamesAreUnique() {
        val wires = Op.ALL.map { it.wire }
        assertEquals(wires.size, wires.toSet().size)
    }

    @Test
    fun newUserNameIsRecognisedAsSpikeUser() {
        val name = SpikeNames.newUserName(42L)
        assertEquals("spike-kid-42", name)
        assertTrue(SpikeNames.isSpikeUser(name))
    }

    @Test
    fun otherNamesAreNotSpikeUsers() {
        assertFalse(SpikeNames.isSpikeUser(null))
        assertFalse(SpikeNames.isSpikeUser("Owner"))
        assertFalse(SpikeNames.isSpikeUser("kid"))
    }

    @Test
    fun defaultPackageIsChrome() {
        assertEquals("com.android.chrome", SpikeNames.DEFAULT_PACKAGE)
    }
}
