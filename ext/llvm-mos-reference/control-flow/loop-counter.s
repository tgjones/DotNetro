	.zeropage	__rc0
	.zeropage	__rc1
	.zeropage	__rc2
	.zeropage	__rc3
	.zeropage	__rc4
	.zeropage	__rc5
	.zeropage	__rc6
	.zeropage	__rc7
	.zeropage	__rc8
	.zeropage	__rc9
	.zeropage	__rc10
	.zeropage	__rc11
	.zeropage	__rc12
	.zeropage	__rc13
	.zeropage	__rc14
	.zeropage	__rc15
	.zeropage	__rc16
	.zeropage	__rc17
	.zeropage	__rc18
	.zeropage	__rc19
	.zeropage	__rc20
	.zeropage	__rc21
	.zeropage	__rc22
	.zeropage	__rc23
	.zeropage	__rc24
	.zeropage	__rc25
	.zeropage	__rc26
	.zeropage	__rc27
	.zeropage	__rc28
	.zeropage	__rc29
	.zeropage	__rc30
	.zeropage	__rc31
	.file	"loop-counter.c"
	.section	.text.loop_counter,"ax",@progbits
	.globl	loop_counter                    ; -- Begin function loop_counter
	.type	loop_counter,@function
loop_counter:                           ; @loop_counter
; %bb.0:
	stx	__rc2
	tay
	cmp	#1
	txa
	sbc	#0
	bvc	.LBB0_2
; %bb.1:
	eor	#128
.LBB0_2:
	cmp	#0
	bpl	.LBB0_3
	jmp	.LBB0_8
.LBB0_3:
	lda	__rc20
	pha
	sty	__rc20
	sty	__rc8
	lda	#255
	dec	__rc8
	cmp	__rc8
	bne	.LBB0_5
; %bb.4:
	dex
.LBB0_5:
	clc
	tya
	adc	#254
	sta	__rc4
	lda	__rc2
	adc	#255
	ldy	__rc2
	sty	.Lloop_counter_sstk             ; 1-byte Folded Spill
	ldy	#0
	sty	__rc2
	sty	__rc3
	sta	__rc5
	sty	__rc6
	sty	__rc7
	lda	__rc8
	jsr	__mulsi3
	sta	__rc3
	stx	__rc4
	lda	__rc2
	and	#1
	lsr
	lda	__rc20
	ror	__rc4
	ror	__rc3
	clc
	adc	__rc3
	tay
	ldx	.Lloop_counter_sstk             ; 1-byte Folded Reload
	stx	__rc2
	lda	__rc4
	adc	__rc2
	dey
	cpy	#255
	bne	.LBB0_7
; %bb.6:
	clc
	adc	#255
.LBB0_7:
	tax
	tya
	sta	__rc16
	pla
	sta	__rc20
	lda	__rc16
	rts
.LBB0_8:
	lda	#0
	tax
	rts
.Lfunc_end0:
	.size	loop_counter, .Lfunc_end0-loop_counter
                                        ; -- End function
	.type	.Lstatic_stack,@object          ; @static_stack
	.section	.noinit..Lstatic_stack,"aw",@nobits
.Lstatic_stack:
	.zero	1
	.size	.Lstatic_stack, 1

.Lloop_counter_sstk = .Lstatic_stack
	.size	.Lloop_counter_sstk, 1
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	.addrsig_sym .Lstatic_stack
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
